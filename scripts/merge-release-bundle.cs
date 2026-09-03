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
var sourceCommitOption = new Option<string>("--source-commit")
{
    Description = "Expected source commit for every asset.",
    Required = true
};

var command = new RootCommand("Merge per-asset metadata into a release bundle.");
command.Options.Add(inputDirectoryOption);
command.Options.Add(outputDirectoryOption);
command.Options.Add(releaseVersionOption);
command.Options.Add(sourceCommitOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var inputDirectory = Path.GetFullPath(parseResult.GetValue(inputDirectoryOption)!);
    var outputDirectory = Path.GetFullPath(parseResult.GetValue(outputDirectoryOption)!);
    var releaseVersion = parseResult.GetValue(releaseVersionOption)!;
    var expectedSourceCommit = parseResult.GetValue(sourceCommitOption)!;
    if (expectedSourceCommit.Length != 40 || !expectedSourceCommit.All(Uri.IsHexDigit))
        throw new InvalidOperationException("Source commit must be a 40-character hexadecimal commit ID.");

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
    string? sourceCommit = null;
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

        if (!string.Equals(metadata.SourceCommit, expectedSourceCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Metadata file '{metadataFile}' reported source commit '{metadata.SourceCommit}' instead of '{expectedSourceCommit}'.");
        }

        sourceCommit ??= metadata.SourceCommit;
        if (!string.Equals(sourceCommit, metadata.SourceCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Per-asset metadata contains inconsistent source commits.");

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
    if (sortedAssets.Select(asset => asset.RuntimeIdentifier).Distinct(StringComparer.Ordinal).Count() != sortedAssets.Length)
        throw new InvalidOperationException("Release bundle contains duplicate runtime identifiers.");

    File.WriteAllLines(
        Path.Combine(outputDirectory, "checksums.txt"),
        sortedAssets.Select(asset => $"{asset.Sha256}  {asset.Name}"));

    var releaseMetadata = new ReleaseMetadata(
        releaseVersion,
        sourceCommit ?? throw new InvalidOperationException("Release bundle source commit is missing."),
        false,
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
    string Sha256,
    string? SourceCommit);

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
    string SourceCommit,
    bool WindowsSigned,
    IReadOnlyList<ReleaseAsset> Assets,
    IReadOnlyList<ReleaseAsset> WindowsAssets);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AssetMetadata))]
[JsonSerializable(typeof(ReleaseMetadata))]
internal sealed partial class BundleJsonContext : JsonSerializerContext;
