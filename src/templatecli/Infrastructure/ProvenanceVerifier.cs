using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sigstore;

namespace TemplateCli.Infrastructure;

/// <summary>
/// Verifies provenance of downloaded binaries.
/// Windows: Authenticode signature + certificate chain via embedded PowerShell script.
/// Linux/macOS: GitHub artifact attestations verified locally from GitHub bundle data via Sigstore.
/// Also handles SHA256 checksum and release-metadata.json validation (cross-platform).
/// </summary>
public sealed class ProvenanceVerifier
{
    private readonly ILogger<ProvenanceVerifier> _logger;
    private readonly SigstoreVerifier _sigstoreVerifier = new();
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(60);
    private const string GitHubActionsOidcIssuer = "https://token.actions.githubusercontent.com";
    private const string TrustedReleaseWorkflowFile = "release.yml";
    private static readonly HttpClient GitHubApiClient = CreateGitHubApiClient();

    public ProvenanceVerifier(ILogger<ProvenanceVerifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verifies provenance of a binary file.
    /// On Windows: Authenticode signature and certificate chain via embedded PowerShell script.
    /// On Linux/macOS: GitHub artifact attestation bundle verification via Sigstore.
    /// </summary>
    /// <returns>True if verification passed, false if it failed.</returns>
    public async Task<(bool Success, string? Error)> VerifyBinaryTrustAsync(string binaryPath, string? sourceRef, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return await VerifyAuthenticodeAsync(binaryPath, ct);

        return await VerifyAttestationAsync(binaryPath, sourceRef, ct);
    }

    /// <summary>
    /// Verifies GitHub artifact attestation for a file using the GitHub attestation bundle API
    /// and local Sigstore verification.
    /// Used on Linux/macOS where Authenticode is not available.
    /// Mirrors the verification done by the template install script.
    /// Tries each allowed workflow identity that may have produced the release asset.
    /// </summary>
    public async Task<(bool Success, string? Error)> VerifyAttestationAsync(string filePath, string? sourceRef, CancellationToken ct)
    {
        _logger.LogInformation("Verifying artifact attestation for '{FilePath}'", filePath);

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppIdentity.UpdateSourceEnvVar)))
        {
            var message = $"Sigstore attestation verification is only supported for GitHub release sources. Use --skip-provenance-checks when {AppIdentity.UpdateSourceEnvVar} points at a local directory.";
            _logger.LogWarning("{Message}", message);
            return (false, message);
        }

        var repo = GitHubReleaseService.Repository;
        if (!TryParseRepository(repo, out var owner, out var repository))
        {
            var message = $"Repository '{repo}' must be in 'owner/name' format to verify attestations.";
            _logger.LogWarning("{Message}", message);
            return (false, message);
        }

        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            var message = "An expected Git ref is required to verify GitHub release attestations on Linux/macOS.";
            _logger.LogWarning("{Message}", message);
            return (false, message);
        }

        var digest = await ComputeSha256Async(filePath, ct);
        if (digest is null)
            return (false, $"Failed to compute SHA256 for '{filePath}'.");

        var (bundleJson, bundleError) = await DownloadAttestationBundleAsync(owner, repository, digest, ct);
        if (bundleJson is null)
        {
            _logger.LogWarning("Failed to download attestation bundle for '{FilePath}': {Error}", filePath, bundleError);
            return (false, bundleError ?? "Failed to download attestation bundle.");
        }

        SigstoreBundle bundle;
        try
        {
            bundle = SigstoreBundle.Deserialize(bundleJson);
        }
        catch (JsonException ex)
        {
            var message = $"GitHub returned an invalid Sigstore bundle: {ex.Message}";
            _logger.LogWarning(ex, "{Message}", message);
            return (false, message);
        }

        var policy = CreateGitHubActionsPolicy(owner, repository, TrustedReleaseWorkflowFile, sourceRef);
        await using var artifactStream = File.OpenRead(filePath);
        var (success, result) = await _sigstoreVerifier.TryVerifyStreamAsync(artifactStream, bundle, policy);
        if (success)
        {
            _logger.LogInformation("Artifact attestation verification passed for '{FilePath}' using workflow '{WorkflowFile}' at '{SourceRef}'", filePath, TrustedReleaseWorkflowFile, sourceRef);
            return (true, null);
        }

        var lastError = result?.FailureReason;
        _logger.LogWarning("Attestation verification failed for '{FilePath}': {Error}", filePath, lastError);
        return (false, lastError ?? "Attestation verification failed");
    }

    private async Task<string?> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexStringLower(hashBytes);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to compute SHA256 for '{FilePath}'", filePath);
            return null;
        }
    }

    private async Task<(string? BundleJson, string? Error)> DownloadAttestationBundleAsync(
        string owner,
        string repository,
        string digest,
        CancellationToken ct)
    {
        var endpoint = $"https://api.github.com/repos/{owner}/{repository}/dependency-graph/artifact-attestations/sha256/{digest}/bundle";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(VerifyTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.CommandName, typeof(ProvenanceVerifier).Assembly.GetName().Version?.ToString() ?? "0.0.0"));

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await GitHubApiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"Timed out downloading attestation bundle for sha256:{digest}.");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Failed to download attestation bundle: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, $"No GitHub attestation bundle was found for sha256:{digest} in {owner}/{repository}.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (null, $"GitHub denied access to the attestation bundle for sha256:{digest}. Set GITHUB_TOKEN or GH_TOKEN when verifying private repositories or when public API rate limits are exceeded.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                return (null, $"GitHub attestation bundle request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}");
            }

            return (await response.Content.ReadAsStringAsync(timeoutCts.Token), null);
        }
    }

    private static VerificationPolicy CreateGitHubActionsPolicy(string owner, string repository, string workflowFile, string sourceRef)
        => new()
        {
            CertificateIdentity = new CertificateIdentity
            {
                Issuer = GitHubActionsOidcIssuer,
                SubjectAlternativeNamePattern =
                    $"^https://github\\.com/{Regex.Escape(owner)}/{Regex.Escape(repository)}/\\.github/workflows/{Regex.Escape(workflowFile)}@{Regex.Escape(sourceRef)}$",
                Extensions = new CertificateExtensionPolicy
                {
                    SourceRepositoryUri = $"https://github.com/{owner}/{repository}",
                    SourceRepositoryRef = sourceRef,
                },
            },
        };

    private static bool TryParseRepository(string repository, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;

        var parts = repository.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        owner = parts[0];
        name = parts[1];
        return true;
    }

    private static HttpClient CreateGitHubApiClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppIdentity.CommandName, typeof(ProvenanceVerifier).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        return client;
    }

    /// <summary>
    /// Verifies the Authenticode signature and certificate chain of a binary (Windows only)
    /// by writing the embedded verify-provenance.ps1 to a temp file and executing it.
    /// </summary>
    private async Task<(bool Success, string? Error)> VerifyAuthenticodeAsync(string binaryPath, CancellationToken ct)
    {
        var scriptContent = GetEmbeddedScript();
        if (scriptContent is null)
            return (false, "Failed to load embedded verification script");

        _logger.LogInformation("Verifying Authenticode provenance of '{BinaryPath}'", binaryPath);

        // Write the embedded script to a temp file for execution. We use a temp file
        // because -EncodedCommand exceeds the 32K command-line limit and -Command -
        // (stdin) can't reliably parse complex multi-line scripts with Add-Type.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{AppIdentity.CommandName}-verify-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, scriptContent, ct);

            var psi = new ProcessStartInfo
            {
                FileName = GetWindowsPowerShellPath(),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -BinaryPath \"{binaryPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // When the app is launched from pwsh, the parent process can carry PowerShell 7
            // module paths that make Windows PowerShell 5.1 autoload the wrong security module.
            psi.Environment["PSModulePath"] = GetWindowsPowerShellModulePath();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(VerifyTimeout);

            using var process = Process.Start(psi)!;
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Provenance verification timed out after {Timeout}s", VerifyTimeout.TotalSeconds);
                process.Kill();
                return (false, "Provenance verification timed out");
            }

            var stdout = await stdoutTask;

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Provenance verification passed for '{BinaryPath}'", binaryPath);
                return (true, null);
            }

            // Try to parse JSON error from stdout
            var error = "Provenance verification failed";
            try
            {
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    using var doc = JsonDocument.Parse(stdout);
                    if (doc.RootElement.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                        error = errorEl.GetString() ?? error;
                }
            }
            catch
            {
                // Fall back to stderr
                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                    error = stderr.Trim();
            }

            _logger.LogWarning("Provenance verification failed for '{BinaryPath}': {Error}", binaryPath, error);
            return (false, error);
        }
        finally
        {
            try { File.Delete(scriptPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Verifies that a file's SHA256 hash matches an expected value from checksums.txt.
    /// </summary>
    public (bool Success, string? Error) VerifyChecksum(string filePath, string checksumsPath, string assetName)
    {
        _logger.LogDebug("Verifying SHA256 checksum for '{AssetName}'", assetName);

        string expectedHash;
        try
        {
            var lines = File.ReadAllLines(checksumsPath);
            expectedHash = ParseExpectedHash(lines, assetName);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to read checksums.txt: {ex.Message}");
        }

        string actualHash;
        try
        {
            using var stream = File.OpenRead(filePath);
            var hashBytes = SHA256.HashData(stream);
            actualHash = Convert.ToHexStringLower(hashBytes);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to compute SHA256 hash: {ex.Message}");
        }

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"SHA256 mismatch for '{assetName}'. Expected '{expectedHash}' but got '{actualHash}'.";
            _logger.LogWarning("{Message}", msg);
            return (false, msg);
        }

        _logger.LogDebug("SHA256 checksum verified for '{AssetName}'", assetName);
        return (true, null);
    }

    /// <summary>
    /// Validates that release-metadata.json agrees with checksums.txt for a given asset.
    /// </summary>
    public (bool Success, string? Error) ValidateReleaseMetadata(string metadataPath, string assetName, string expectedSha256)
    {
        _logger.LogDebug("Validating release metadata for '{AssetName}'", assetName);

        try
        {
            var json = File.ReadAllText(metadataPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("assets", out var assets))
                return (false, "release-metadata.json does not contain 'assets' array");

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameEl) &&
                    string.Equals(nameEl.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!asset.TryGetProperty("sha256", out var sha256El))
                        return (false, $"release-metadata.json asset '{assetName}' missing sha256 field");

                    var metadataSha = sha256El.GetString()?.ToLowerInvariant();
                    if (!string.Equals(metadataSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        return (false, $"release-metadata.json SHA256 for '{assetName}' ({metadataSha}) does not match checksums.txt ({expectedSha256})");

                    _logger.LogDebug("Release metadata validated for '{AssetName}'", assetName);
                    return (true, null);
                }
            }

            return (false, $"release-metadata.json did not contain asset '{assetName}'");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to parse release-metadata.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the embedded verify-provenance.ps1 resource and returns its content as a string.
    /// </summary>
    private string? GetEmbeddedScript()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("verify-provenance.ps1", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                _logger.LogError("Embedded verify-provenance.ps1 resource not found");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read embedded verification script");
            return null;
        }
    }

    private static string GetWindowsPowerShellPath()
    {
        var candidatePath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidatePath) ? candidatePath : "powershell.exe";
    }

    private static string GetWindowsPowerShellModulePath()
    {
        var modulePaths = new List<string>();

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documentsPath))
            modulePaths.Add(Path.Combine(documentsPath, "WindowsPowerShell", "Modules"));

        var programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFilesPath))
            modulePaths.Add(Path.Combine(programFilesPath, "WindowsPowerShell", "Modules"));

        if (!string.IsNullOrWhiteSpace(Environment.SystemDirectory))
            modulePaths.Add(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"));

        return string.Join(
            Path.PathSeparator,
            modulePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string ParseExpectedHash(string[] lines, string assetName)
    {
        foreach (var line in lines)
        {
            // Format: "<sha256hash>  <filename>" or "<sha256hash> *<filename>"
            if (!line.Contains(assetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Equals(assetName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64)
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InvalidOperationException($"checksums.txt did not contain an entry for '{assetName}'.");
    }
}
