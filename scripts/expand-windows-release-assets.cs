#!/usr/bin/env dotnet

#:package System.CommandLine
#:property PublishAot=false

using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

var bundleDirectoryOption = new Option<string>("--bundle-directory")
{
    Description = "Release bundle directory containing release-metadata.json.",
    Required = true
};
var workingDirectoryOption = new Option<string>("--working-directory")
{
    Description = "Directory where Windows assets should be expanded.",
    Required = true
};

var command = new RootCommand("Expand Windows release archives and emit a staging manifest.");
command.Options.Add(bundleDirectoryOption);
command.Options.Add(workingDirectoryOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var bundleDirectory = Path.GetFullPath(parseResult.GetValue(bundleDirectoryOption)!);
    var workingDirectory = Path.GetFullPath(parseResult.GetValue(workingDirectoryOption)!);
    var metadataPath = Path.Combine(bundleDirectory, "release-metadata.json");

    EnsureDirectoryExists(bundleDirectory, "Bundle directory");
    EnsureFileExists(metadataPath, "Release metadata file");

    var metadata = JsonSerializer.Deserialize(
        File.ReadAllText(metadataPath),
        WindowsAssetsJsonContext.Default.ReleaseMetadataDocument)
        ?? throw new InvalidOperationException($"Release metadata file '{metadataPath}' could not be parsed.");

    var windowsAssets = metadata.WindowsAssets.Count > 0
        ? metadata.WindowsAssets
        : metadata.Assets.Where(asset => string.Equals(asset.Platform, "win", StringComparison.Ordinal)).ToArray();

    if (windowsAssets.Count == 0)
    {
        throw new InvalidOperationException($"No Windows assets were found in '{metadataPath}'.");
    }

    Directory.CreateDirectory(workingDirectory);

    var manifestEntries = new List<WindowsAssetManifestEntry>();
    foreach (var asset in windowsAssets)
    {
        if (string.IsNullOrWhiteSpace(asset.RuntimeIdentifier))
        {
            throw new InvalidOperationException($"Windows asset '{asset.Name}' is missing a runtimeIdentifier.");
        }

        var archivePath = Path.Combine(bundleDirectory, asset.Name);
        EnsureFileExists(archivePath, "Archive");

        var assetDirectory = Path.Combine(workingDirectory, asset.RuntimeIdentifier);
        if (Directory.Exists(assetDirectory))
        {
            Directory.Delete(assetDirectory, recursive: true);
        }

        Directory.CreateDirectory(assetDirectory);
        ZipFile.ExtractToDirectory(archivePath, assetDirectory, overwriteFiles: true);

        var binaryPath = Path.Combine(assetDirectory, "templatecli.exe");
        EnsureFileExists(binaryPath, "Expanded Windows asset");
        EnsureFileExists(Path.Combine(assetDirectory, "payload-manifest.json"), "Expanded Windows asset");
        EnsurePayloadManifestMatches(assetDirectory);

        manifestEntries.Add(new WindowsAssetManifestEntry(asset.Name, asset.RuntimeIdentifier, assetDirectory));
    }

    File.WriteAllText(
        Path.Combine(workingDirectory, "windows-assets-manifest.json"),
        JsonSerializer.Serialize(manifestEntries, WindowsAssetsJsonContext.Default.ListWindowsAssetManifestEntry));
}));

return command.Parse(args).Invoke();

static void ExecuteHandled(Action action)
{
    try
    {
        action();
    }
    catch (ArgumentException ex)
    {
        Fail(ex.Message);
    }
    catch (DirectoryNotFoundException ex)
    {
        Fail(ex.Message);
    }
    catch (FileNotFoundException ex)
    {
        Fail(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        Fail(ex.Message);
    }
    catch (IOException ex)
    {
        Fail(ex.Message);
    }
    catch (JsonException ex)
    {
        Fail($"Invalid JSON input: {ex.Message}");
    }
}

static void EnsureDirectoryExists(string path, string description)
{
    if (!Directory.Exists(path))
    {
        throw new DirectoryNotFoundException($"{description} '{path}' was not found.");
    }
}

static void EnsureFileExists(string path, string description)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"{description} '{path}' was not found.");
    }
}

static void EnsurePayloadManifestMatches(string directory)
    {
        const string manifestName = "payload-manifest.json";
        var manifestPath = Path.Combine(directory, manifestName);
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            WindowsAssetsJsonContext.Default.PayloadManifest)
            ?? throw new InvalidOperationException($"Payload manifest '{manifestPath}' could not be parsed.");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousEntry = null;
        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Contains('\\'))
                throw new InvalidOperationException($"Payload manifest contains invalid path '{entry}'.");
            if (previousEntry is not null && StringComparer.Ordinal.Compare(previousEntry, entry) > 0)
                throw new InvalidOperationException("Payload manifest paths are not sorted using ordinal comparison.");
            previousEntry = entry;
            var fullPath = Path.GetFullPath(Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Payload manifest path '{entry}' is outside the payload.");
            if (!declared.Add(Path.GetRelativePath(directory, fullPath)))
                throw new InvalidOperationException($"Payload manifest contains duplicate path '{entry}'.");
            EnsureFileExists(fullPath, "Payload manifest entry");
        }
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .Where(path => !path.Equals(manifestName, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (declared.Count == 0 || !declared.SetEquals(actual))
            throw new InvalidOperationException($"Payload manifest '{manifestPath}' does not exactly describe the payload.");
}

static void Fail(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    Environment.Exit(1);
}

internal sealed record ReleaseMetadataDocument(
    string Version,
    IReadOnlyList<ReleaseAsset> Assets,
    IReadOnlyList<ReleaseAsset> WindowsAssets);

internal sealed record ReleaseAsset(
    string Name,
    string RuntimeIdentifier,
    string Platform,
    string Architecture,
    string FileType,
    string CommandName,
    string Sha256);

internal sealed record WindowsAssetManifestEntry(
    string AssetName,
    string RuntimeIdentifier,
    string StagingDirectory);

internal sealed record PayloadManifest(IReadOnlyList<string> Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ReleaseMetadataDocument))]
[JsonSerializable(typeof(List<WindowsAssetManifestEntry>))]
[JsonSerializable(typeof(PayloadManifest))]
internal sealed partial class WindowsAssetsJsonContext : JsonSerializerContext;
