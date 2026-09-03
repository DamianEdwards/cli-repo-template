using System.Formats.Tar;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

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
    public bool IsLocalSource => !string.IsNullOrWhiteSpace(LocalSource);

    public GitHubReleaseService(ILogger<GitHubReleaseService> logger)
    {
        _logger = logger;
        LocalSource = Environment.GetEnvironmentVariable(AppIdentity.UpdateSourceEnvVar);
    }

    public ReleaseInfo? GetLatestRelease(
        NuGetVersion currentVersion,
        bool allowPreRelease,
        bool stableOnly,
        string assetName)
    {
        if (IsLocalSource)
            return GetLocalRelease(currentVersion, allowPreRelease, stableOnly, assetName);

        using var doc = GetReleasesDocument();
        return SelectLatestRelease(
            doc.RootElement,
            currentVersion,
            allowPreRelease,
            stableOnly,
            assetName);
    }

    public static ReleaseInfo? SelectLatestRelease(
        JsonElement releases,
        NuGetVersion currentVersion,
        bool allowPreRelease,
        bool stableOnly,
        string assetName)
    {
        var candidates = new List<(NuGetVersion Version, ReleaseInfo Release)>();

        foreach (var release in releases.EnumerateArray())
        {
            var isDraft = release.TryGetProperty("draft", out var d) && d.GetBoolean();
            if (isDraft)
                continue;

            var tagName = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!VersionHelper.TryParse(tagName, out var version)
                || tagName.StartsWith("install-scripts-v", StringComparison.OrdinalIgnoreCase)
                || !ReleaseHasAsset(release, assetName)
                || !VersionHelper.IsUpdateCandidate(currentVersion, version, allowPreRelease, stableOnly))
                continue;

            var isPrerelease = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            var name = release.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            candidates.Add((version, new ReleaseInfo(tagName, name, isPrerelease, isDraft)));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version, VersionComparer.VersionRelease)
            .Select(candidate => candidate.Release)
            .FirstOrDefault();
    }

    private ReleaseInfo? GetLocalRelease(
        NuGetVersion currentVersion,
        bool allowPreRelease,
        bool stableOnly,
        string assetName)
    {
        if (!Directory.Exists(LocalSource))
            throw new UserFacingException($"Local update source '{LocalSource}' does not exist.");

        var assetPath = Path.Combine(LocalSource!, assetName);
        if (!File.Exists(assetPath))
            throw new UserFacingException($"Local update source '{LocalSource}' does not contain '{assetName}'.");

        var metadataPath = Path.Combine(LocalSource!, "release-metadata.json");
        if (!File.Exists(metadataPath))
            throw new UserFacingException($"Local update source '{LocalSource}' does not contain release-metadata.json.");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var versionText = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (!VersionHelper.TryParse(versionText, out var version))
                throw new UserFacingException($"Local update source reported invalid version '{versionText}'.");

            if (!VersionHelper.IsUpdateCandidate(currentVersion, version, allowPreRelease, stableOnly))
                return null;

            var isPrerelease = doc.RootElement.TryGetProperty("prerelease", out var p) && p.GetBoolean();

            _logger.LogInformation("Using local release: version={Version}, asset={Asset}", versionText, assetName);
            return new ReleaseInfo(version.ToNormalizedString(), $"Local build ({version})", isPrerelease, false);
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Local release-metadata.json is invalid.", ex);
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
            ExtractTarGzipSafely(archivePath, destinationDir);
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

    private static void ExtractTarGzipSafely(string archivePath, string destinationDir)
    {
        var root = Path.GetFullPath(destinationDir)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            var name = entry.Name
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            while (name.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                name = name[2..];

            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (Path.IsPathRooted(name))
                throw new UserFacingException($"Update archive contains absolute path '{entry.Name}'.");

            var destinationPath = Path.GetFullPath(Path.Combine(destinationDir, name));
            if (!destinationPath.StartsWith(root, StringComparison.Ordinal))
                throw new UserFacingException($"Update archive contains path outside its payload: '{entry.Name}'.");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destinationPath);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    entry.ExtractToFile(destinationPath, overwrite: true);
                    break;
                case TarEntryType.ExtendedAttributes:
                case TarEntryType.GlobalExtendedAttributes:
                    break;
                default:
                    throw new UserFacingException(
                        $"Update archive contains unsupported entry '{entry.Name}' ({entry.EntryType}).");
            }
        }
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

    private JsonDocument GetReleasesDocument()
    {
        var content = SendGitHubApiRequest($"https://api.github.com/repos/{Repository}/releases?per_page=100", ApiTimeout);
        return JsonDocument.Parse(content
            ?? throw new UserFacingException("GitHub returned no release data."));
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
                throw new UserFacingException(
                    $"GitHub API request failed with {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new UserFacingException($"GitHub API request to '{url}' failed.", ex);
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
