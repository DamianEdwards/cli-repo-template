using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TemplateCli.Infrastructure;
using TemplateCli.Models;

namespace TemplateCli.Services;

public static class PayloadInstaller
{
    public const string ManifestFileName = "payload-manifest.json";

    public static IReadOnlyList<string> ValidateManifest(string payloadDirectory)
    {
        var manifestPath = Path.Combine(payloadDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new UserFacingException($"Update payload does not contain {ManifestFileName}.");

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            TemplateCliJsonContext.Default.PayloadManifest)
            ?? throw new UserFacingException($"Update payload contains an invalid {ManifestFileName}.");
        if (manifest.Files.Count == 0)
            throw new UserFacingException($"Update payload contains an empty {ManifestFileName}.");

        var declared = new HashSet<string>(StringComparer.Ordinal);
        string? previousEntry = null;
        foreach (var entry in manifest.Files)
        {
            if (entry.Contains('\\'))
                throw new UserFacingException($"{ManifestFileName} path '{entry}' is not slash-normalized.");
            if (previousEntry is not null
                && StringComparer.Ordinal.Compare(previousEntry, entry) >= 0)
            {
                throw new UserFacingException(
                    $"{ManifestFileName} paths must be sorted using ordinal comparison and contain no duplicates.");
            }
            var normalized = NormalizeRelativePath(payloadDirectory, entry);
            if (!declared.Add(entry))
                throw new UserFacingException($"{ManifestFileName} contains duplicate path '{entry}'.");

            if (!File.Exists(Path.Combine(payloadDirectory, normalized)))
                throw new UserFacingException($"Update payload is missing declared file '{entry}'.");
            previousEntry = entry;
        }

        var actual = Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(payloadDirectory, path))
            .Where(path => !path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(actual))
        {
            var undeclared = actual.Except(declared, StringComparer.Ordinal).ToArray();
            throw new UserFacingException(
                undeclared.Length > 0
                    ? $"Update payload contains undeclared file(s): {string.Join(", ", undeclared)}."
                    : "Update payload manifest does not match the extracted archive.");
        }

        var executableFileName = AppIdentity.GetExecutableFileName();
        if (!declared.Contains(executableFileName))
            throw new UserFacingException(
                $"Update payload manifest does not contain required file '{executableFileName}'.");

        return manifest.Files
            .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
            .ToArray();
    }

    public static IReadOnlyList<string> ReadManagedFiles(string installDirectory)
    {
        var manifestPath = Path.Combine(installDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            return [];

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            TemplateCliJsonContext.Default.PayloadManifest)
            ?? throw new UserFacingException($"Installed {ManifestFileName} is invalid.");
        return manifest.Files
            .Select(entry => NormalizeRelativePath(installDirectory, entry))
            .ToArray();
    }

    public static void BackupFiles(
        string sourceDirectory,
        string backupDirectory,
        IEnumerable<string> files)
    {
        foreach (var relativePath in files)
        {
            var sourcePath = Path.Combine(sourceDirectory, relativePath);
            if (!File.Exists(sourcePath))
                continue;

            var backupPath = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Move(sourcePath, backupPath, overwrite: true);
        }
    }

    public static void DeleteFiles(string directory, IEnumerable<string> files)
    {
        foreach (var relativePath in files)
        {
            var path = Path.Combine(directory, relativePath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public static void CopyFiles(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<string> files)
    {
        foreach (var relativePath in files)
        {
            var sourcePath = Path.Combine(sourceDirectory, relativePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    public static void RestoreFiles(
        string installDirectory,
        string backupDirectory,
        IEnumerable<string> managedFiles)
    {
        DeleteFiles(installDirectory, managedFiles);
        if (!Directory.Exists(backupDirectory))
            return;

        foreach (var backupPath in Directory.EnumerateFiles(
                     backupDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(backupDirectory, backupPath);
            var destinationPath = Path.Combine(installDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(backupPath, destinationPath, overwrite: true);
        }
    }

    public static async Task VerifyInstalledPayloadAsync(
        string installDirectory,
        string expectedVersion,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ValidateManifest(installDirectory);
        var executablePath = Path.Combine(installDirectory, AppIdentity.GetExecutableFileName());
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo)
            ?? throw new UserFacingException($"Could not start updated {AppIdentity.ProductName} for validation.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new UserFacingException(
                $"Updated {AppIdentity.ProductName} did not pass its startup/version check in time.");
        }

        var output = (await stdoutTask).Trim();
        var error = (await stderrTask).Trim();
        if (process.ExitCode != 0)
            throw new UserFacingException(
                $"Updated {AppIdentity.ProductName} failed its startup/version check: {error}");

        var candidate = output.Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token => VersionHelper.TryParse(token, out _));
        if (!VersionHelper.TryParse(candidate, out var actual)
            || !VersionHelper.TryParse(expectedVersion, out var expected)
            || actual != expected)
        {
            throw new UserFacingException(
                $"Updated binary reported version '{output}', expected '{expectedVersion}'.");
        }

        logger.LogDebug(
            "Validated installed {ProductName} payload version {Version}",
            AppIdentity.ProductName,
            expectedVersion);
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new UserFacingException($"{ManifestFileName} contains invalid path '{path}'.");

        var normalized = path
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var rootPath = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException($"{ManifestFileName} contains path outside the payload: '{path}'.");

        return Path.GetRelativePath(root, fullPath);
    }
}
