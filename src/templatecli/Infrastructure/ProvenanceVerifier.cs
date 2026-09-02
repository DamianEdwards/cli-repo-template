using System.Diagnostics;
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

    public ProvenanceVerifier(ILogger<ProvenanceVerifier> logger)
    {
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> VerifyWindowsPayloadAsync(
        string payloadDirectory,
        CancellationToken ct)
    {
        foreach (var fileName in GetWindowsExecutablePayloadFileNames(
                     Services.PayloadInstaller.ValidateManifest(payloadDirectory)))
        {
            var result = await VerifyAuthenticodeAsync(Path.Combine(payloadDirectory, fileName), ct);
            if (!result.Success)
                return result;
        }

        return (true, null);
    }

    public static IReadOnlyList<string> GetWindowsExecutablePayloadFileNames(
        IEnumerable<string> payloadFiles)
        => payloadFiles
            .Where(AppIdentity.IsWindowsExecutablePayloadFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<(bool Success, string? Error)> VerifyArchiveAttestationAsync(
        string filePath,
        string sourceRef,
        string bundlePath,
        CancellationToken ct)
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

        if (!File.Exists(bundlePath))
            return (false, "The release does not contain its portable Sigstore attestation bundle.");

        string[] bundleLines;
        try
        {
            bundleLines = await File.ReadAllLinesAsync(bundlePath, ct);
        }
        catch (IOException ex)
        {
            return (false, $"The release Sigstore attestation bundle could not be read: {ex.Message}");
        }

        var policy = CreateGitHubActionsPolicy(owner, repository, TrustedReleaseWorkflowFile, sourceRef);
        var failures = new List<string>();
        foreach (var bundleJson in bundleLines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var bundle = SigstoreBundle.Deserialize(bundleJson);
                await using var artifactStream = File.OpenRead(filePath);
                var (success, result) =
                    await _sigstoreVerifier.TryVerifyStreamAsync(artifactStream, bundle, policy);
                if (success)
                {
                    _logger.LogInformation(
                        "Artifact attestation verification passed for '{FilePath}' using workflow '{WorkflowFile}' at '{SourceRef}'",
                        filePath,
                        TrustedReleaseWorkflowFile,
                        sourceRef);
                    return (true, null);
                }

                if (!string.IsNullOrWhiteSpace(result?.FailureReason))
                    failures.Add(result.FailureReason);
            }
            catch (JsonException ex)
            {
                failures.Add(ex.Message);
            }
        }

        var lastError = failures.Count == 0 ? null : failures[^1];
        _logger.LogWarning("Attestation verification failed for '{FilePath}': {Error}", filePath, lastError);
        return (false, lastError ?? "Attestation verification failed");
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
                await process.WaitForExitAsync(CancellationToken.None);
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
    public (bool Success, string? Error) ValidateReleaseMetadata(
        string metadataPath,
        string assetName,
        string expectedSha256,
        string expectedVersion)
    {
        _logger.LogDebug("Validating release metadata for '{AssetName}'", assetName);

        try
        {
            var json = File.ReadAllText(metadataPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("version", out var version)
                || !string.Equals(
                    version.GetString()?.TrimStart('v'),
                    expectedVersion.TrimStart('v'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"release-metadata.json did not identify expected version '{expectedVersion}'");
            }

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

    public static string GetWindowsPowerShellModulePath()
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
                && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InvalidOperationException($"checksums.txt did not contain an entry for '{assetName}'.");
    }
}
