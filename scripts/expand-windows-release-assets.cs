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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ReleaseMetadataDocument))]
[JsonSerializable(typeof(List<WindowsAssetManifestEntry>))]
internal sealed partial class WindowsAssetsJsonContext : JsonSerializerContext;
