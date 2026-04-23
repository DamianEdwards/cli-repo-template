#!/usr/bin/env dotnet

#:package System.CommandLine
#:property PublishAot=false

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

var inputDirectoryOption = new Option<string>("--input-directory")
{
    Description = "Directory containing per-asset metadata and archive files.",
    Required = true
};
var outputDirectoryOption = new Option<string>("--output-directory")
{
    Description = "Directory where the merged release bundle should be written.",
    Required = true
};
var releaseVersionOption = new Option<string>("--release-version")
{
    Description = "Expected asset version.",
    Required = true
};

var command = new RootCommand("Merge per-asset metadata into a release bundle.");
command.Options.Add(inputDirectoryOption);
command.Options.Add(outputDirectoryOption);
command.Options.Add(releaseVersionOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var inputDirectory = Path.GetFullPath(parseResult.GetValue(inputDirectoryOption)!);
    var outputDirectory = Path.GetFullPath(parseResult.GetValue(outputDirectoryOption)!);
    var releaseVersion = parseResult.GetValue(releaseVersionOption)!;

    EnsureDirectoryExists(inputDirectory, "Input directory");
    Directory.CreateDirectory(outputDirectory);

    var metadataFiles = Directory
        .EnumerateFiles(inputDirectory, "*.json", SearchOption.AllDirectories)
        .Where(path => !string.Equals(Path.GetFileName(path), "release-metadata.json", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    if (metadataFiles.Length == 0)
    {
        throw new InvalidOperationException($"No per-asset metadata files were found under '{inputDirectory}'.");
    }

    var allFiles = Directory
        .EnumerateFiles(inputDirectory, "*", SearchOption.AllDirectories)
        .ToArray();

    var assets = new List<ReleaseAsset>();
    foreach (var metadataFile in metadataFiles)
    {
        var metadata = JsonSerializer.Deserialize(
            File.ReadAllText(metadataFile),
            BundleJsonContext.Default.AssetMetadata)
            ?? throw new InvalidOperationException($"Metadata file '{metadataFile}' could not be parsed.");

        if (!string.Equals(metadata.Version, releaseVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Metadata file '{metadataFile}' reported version '{metadata.Version}' instead of '{releaseVersion}'.");
        }

        var assetPath = allFiles.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), metadata.AssetName, StringComparison.Ordinal));

        if (assetPath is null)
        {
            throw new FileNotFoundException(
                $"Asset '{metadata.AssetName}' referenced by '{metadataFile}' was not found.");
        }

        File.Copy(assetPath, Path.Combine(outputDirectory, Path.GetFileName(assetPath)), overwrite: true);

        assets.Add(new ReleaseAsset(
            metadata.AssetName,
            metadata.RuntimeIdentifier,
            metadata.Platform,
            metadata.Architecture,
            metadata.FileType,
            metadata.CommandName,
            metadata.Sha256));
    }

    var sortedAssets = assets
        .OrderBy(asset => asset.Name, StringComparer.Ordinal)
        .ToArray();

    File.WriteAllLines(
        Path.Combine(outputDirectory, "checksums.txt"),
        sortedAssets.Select(asset => $"{asset.Sha256}  {asset.Name}"));

    var releaseMetadata = new ReleaseMetadata(
        releaseVersion,
        sortedAssets,
        sortedAssets.Where(asset => string.Equals(asset.Platform, "win", StringComparison.Ordinal)).ToArray());

    File.WriteAllText(
        Path.Combine(outputDirectory, "release-metadata.json"),
        JsonSerializer.Serialize(releaseMetadata, BundleJsonContext.Default.ReleaseMetadata));
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

static void Fail(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    Environment.Exit(1);
}

internal sealed record AssetMetadata(
    string Version,
    string RuntimeIdentifier,
    string Platform,
    string Architecture,
    string AssetName,
    string FileType,
    string CommandName,
    string Sha256);

internal sealed record ReleaseAsset(
    string Name,
    string RuntimeIdentifier,
    string Platform,
    string Architecture,
    string FileType,
    string CommandName,
    string Sha256);

internal sealed record ReleaseMetadata(
    string Version,
    IReadOnlyList<ReleaseAsset> Assets,
    IReadOnlyList<ReleaseAsset> WindowsAssets);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AssetMetadata))]
[JsonSerializable(typeof(ReleaseMetadata))]
internal sealed partial class BundleJsonContext : JsonSerializerContext;
