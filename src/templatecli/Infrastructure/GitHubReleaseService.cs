using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TemplateCli.Infrastructure;

public sealed record ReleaseInfo(
    string TagName,
    string Name,
    bool IsPrerelease,
    bool IsDraft);

public sealed class GitHubReleaseService
{
    private readonly ILogger<GitHubReleaseService> _logger;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static string Repository
        => Environment.GetEnvironmentVariable(AppIdentity.UpdateRepositoryEnvVar) is { Length: > 0 } configured
            ? configured
            : AppIdentity.DefaultRepository;

    public string? LocalSource { get; set; }

    public GitHubReleaseService(ILogger<GitHubReleaseService> logger)
    {
        _logger = logger;
        LocalSource = Environment.GetEnvironmentVariable(AppIdentity.UpdateSourceEnvVar);
    }

    public ReleaseInfo? GetLatestRelease(string quality, string assetName)
    {
        if (!string.IsNullOrEmpty(LocalSource))
            return GetLocalRelease(assetName);

        using var doc = GetReleasesDocument();
        if (doc is null)
            return null;

        if (string.Equals(quality, "Dev", StringComparison.OrdinalIgnoreCase))
            return GetDevRelease(doc.RootElement, assetName);

        ReleaseInfo? fallbackPreRelease = null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var isDraft = release.TryGetProperty("draft", out var d) && d.GetBoolean();
            if (isDraft)
                continue;

            var tagName = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (tagName.StartsWith("install-scripts-v", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ReleaseHasAsset(release, assetName))
                continue;

            var isPrerelease = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            var isDevRelease = IsDevTag(tagName);
            var name = release.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var info = new ReleaseInfo(tagName, name, isPrerelease, isDraft);

            if (string.Equals(quality, "PreRelease", StringComparison.OrdinalIgnoreCase))
            {
                if (isPrerelease && !isDevRelease)
                    return info;
                continue;
            }

            if (string.Equals(quality, "Stable", StringComparison.OrdinalIgnoreCase) && isPrerelease)
            {
                if (!isDevRelease)
                    fallbackPreRelease ??= info;
                continue;
            }

            return info;
        }

        if (string.Equals(quality, "Stable", StringComparison.OrdinalIgnoreCase) && fallbackPreRelease is not null)
        {
            _logger.LogWarning("No stable release found, falling back to latest prerelease");
            return fallbackPreRelease;
        }

        _logger.LogWarning("No {Quality} release containing '{AssetName}' was found", quality, assetName);
        return null;
    }

    private ReleaseInfo? GetDevRelease(JsonElement releases, string assetName)
    {
        foreach (var release in releases.EnumerateArray())
        {
            var isDraft = release.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean();
            if (isDraft)
                continue;

            var isPrerelease = release.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();
            if (!isPrerelease)
                continue;

            var tagName = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!IsDevTag(tagName) || !ReleaseHasAsset(release, assetName))
                continue;

            var name = release.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            return new ReleaseInfo(tagName, name, true, false);
        }

        _logger.LogWarning("Could not locate development build release");
        return null;
    }

    private ReleaseInfo? GetLocalRelease(string assetName)
    {
        var assetPath = Path.Combine(LocalSource!, assetName);
        if (!File.Exists(assetPath))
        {
            _logger.LogWarning("Local source '{Path}' does not contain '{Asset}'", LocalSource, assetName);
            return null;
        }

        var metadataPath = Path.Combine(LocalSource!, "release-metadata.json");
        if (!File.Exists(metadataPath))
        {
            _logger.LogWarning("Local source '{Path}' does not contain release-metadata.json", LocalSource);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "local" : "local";
            var isPrerelease = doc.RootElement.TryGetProperty("prerelease", out var p) && p.GetBoolean();

            _logger.LogInformation("Using local release: version={Version}, asset={Asset}", version, assetName);
            return new ReleaseInfo(version, $"Local build ({version})", isPrerelease, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local release-metadata.json");
            return null;
        }
    }

    public string? GetDevReleaseVersion(string tag)
    {
        if (!string.IsNullOrEmpty(LocalSource))
        {
            var localMeta = Path.Combine(LocalSource, "release-metadata.json");
            if (!File.Exists(localMeta))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(localMeta));
                return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        _logger.LogDebug("Fetching release-metadata.json for version from release '{Tag}'", tag);

        var tempDir = Path.Combine(Path.GetTempPath(), $"{AppIdentity.CommandName}-meta-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            if (!DownloadReleaseAsset(tag, "release-metadata.json", tempDir))
                return null;

            var metadataPath = Path.Combine(tempDir, "release-metadata.json");
            var json = File.ReadAllText(metadataPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.String)
                return versionEl.GetString();

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read version from release-metadata.json");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    public bool DownloadReleaseAsset(string tag, string assetName, string destinationDir)
    {
        if (!string.IsNullOrEmpty(LocalSource))
        {
            var localPath = Path.Combine(LocalSource, assetName);
            if (!File.Exists(localPath))
            {
                _logger.LogWarning("Local asset '{AssetName}' not found at '{Path}'", assetName, localPath);
                return false;
            }

            Directory.CreateDirectory(destinationDir);
            File.Copy(localPath, Path.Combine(destinationDir, assetName), overwrite: true);
            _logger.LogDebug("Copied local asset '{AssetName}' from '{Source}'", assetName, localPath);
            return true;
        }

        _logger.LogDebug("Downloading asset '{AssetName}' from release '{Tag}'", assetName, tag);

        Directory.CreateDirectory(destinationDir);
        var assetUrl = GetReleaseAssetDownloadUrl(tag, assetName);
        if (assetUrl is null)
        {
            _logger.LogWarning("Failed to locate '{AssetName}' in release '{Tag}'", assetName, tag);
            return false;
        }

        return DownloadFile(assetUrl, Path.Combine(destinationDir, assetName));
    }

    public static string ExtractReleaseArchive(string archivePath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        var binaryName = AppIdentity.GetExecutableFileName();

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{archivePath}\" -C \"{destinationDir}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start tar process");
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                throw new TimeoutException("tar extraction timed out after 60 seconds");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"tar extraction failed (exit code {process.ExitCode}): {stderr}");
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
        }

        var binaryPath = Path.Combine(destinationDir, binaryName);
        if (!File.Exists(binaryPath))
            throw new FileNotFoundException($"Archive did not contain {binaryName}", binaryPath);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return binaryPath;
    }

    public static string GetPlatformAssetName()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var archStr = arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {arch}")
        };

        if (OperatingSystem.IsWindows())
            return $"{AppIdentity.CommandName}-win-{archStr}.zip";
        if (OperatingSystem.IsMacOS())
            return $"{AppIdentity.CommandName}-osx-{archStr}.tar.gz";
        if (OperatingSystem.IsLinux())
            return $"{AppIdentity.CommandName}-linux-{archStr}.tar.gz";

        throw new PlatformNotSupportedException("Unsupported operating system");
    }

    private JsonDocument? GetReleasesDocument()
    {
        var content = SendGitHubApiRequest($"https://api.github.com/repos/{Repository}/releases?per_page=100", ApiTimeout);
        return content is null ? null : JsonDocument.Parse(content);
    }

    private string? GetReleaseAssetDownloadUrl(string tag, string assetName)
    {
        var content = SendGitHubApiRequest($"https://api.github.com/repos/{Repository}/releases/tags/{tag}", ApiTimeout);
        if (content is null)
            return null;

        using var doc = JsonDocument.Parse(content);
        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var n)
                && string.Equals(n.GetString(), assetName, StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var downloadUrl))
            {
                return downloadUrl.GetString();
            }
        }

        return null;
    }

    private bool DownloadFile(string url, string destinationPath)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var cts = new CancellationTokenSource(DownloadTimeout);

        try
        {
            using var response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub asset download failed with {StatusCode} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);
                return false;
            }

            using var output = File.Create(destinationPath);
            using var stream = response.Content.ReadAsStreamAsync(cts.Token).GetAwaiter().GetResult();
            stream.CopyTo(output);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _logger.LogWarning(ex, "Failed to download GitHub asset from '{Url}'", url);
            return false;
        }
    }

    private string? SendGitHubApiRequest(string url, TimeSpan timeout)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub API request to '{Url}' failed with {StatusCode} {ReasonPhrase}", url, (int)response.StatusCode, response.ReasonPhrase);
                return null;
            }

            return response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub API request to '{Url}' failed", url);
            return null;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.CommandName, typeof(GitHubReleaseService).Assembly.GetName().Version?.ToString() ?? "0.0.0"));

        var token = TryGetGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static string? TryGetGitHubToken()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            if (!process.WaitForExit(5_000))
            {
                process.Kill();
                return null;
            }

            if (process.ExitCode != 0)
                return null;

            var ghToken = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(ghToken) ? null : ghToken;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDevTag(string tagName)
    {
        var normalized = tagName.TrimStart('v');
        return VersionHelper.TryParse(normalized, out var version) && VersionHelper.IsDevBuild(version);
    }

    private static bool ReleaseHasAsset(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets))
            return false;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var n)
                && string.Equals(n.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
