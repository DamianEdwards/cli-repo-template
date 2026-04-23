#!/usr/bin/env dotnet

#:package System.CommandLine
#:property PublishAot=false

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var bundleDirectoryOption = new Option<string>("--bundle-directory")
{
    Description = "Release bundle directory containing release-metadata.json.",
    Required = true
};

var command = new RootCommand("Regenerate release bundle checksums and metadata.");
command.Options.Add(bundleDirectoryOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var bundleDirectory = Path.GetFullPath(parseResult.GetValue(bundleDirectoryOption)!);
    var metadataPath = Path.Combine(bundleDirectory, "release-metadata.json");

    EnsureDirectoryExists(bundleDirectory, "Bundle directory");
    EnsureFileExists(metadataPath, "Release metadata file");

    var metadata = JsonSerializer.Deserialize(
        File.ReadAllText(metadataPath),
        BundleMetadataJsonContext.Default.ReleaseMetadataDocument)
        ?? throw new InvalidOperationException($"Release metadata file '{metadataPath}' could not be parsed.");

    if (string.IsNullOrWhiteSpace(metadata.Version))
    {
        throw new InvalidOperationException($"Release metadata in '{metadataPath}' did not contain a version.");
    }

    var updatedAssets = metadata.Assets
        .Select(asset =>
        {
            var assetPath = Path.Combine(bundleDirectory, asset.Name);
            EnsureFileExists(assetPath, "Release asset");

            return asset with { Sha256 = ComputeSha256(assetPath) };
        })
        .OrderBy(asset => asset.Name, StringComparer.Ordinal)
        .ToArray();

    File.WriteAllLines(
        Path.Combine(bundleDirectory, "checksums.txt"),
        updatedAssets.Select(asset => $"{asset.Sha256}  {asset.Name}"));

    var updatedMetadata = new ReleaseMetadataDocument(
        metadata.Version,
        updatedAssets,
        updatedAssets.Where(asset => string.Equals(asset.Platform, "win", StringComparison.Ordinal)).ToArray());

    File.WriteAllText(
        metadataPath,
        JsonSerializer.Serialize(updatedMetadata, BundleMetadataJsonContext.Default.ReleaseMetadataDocument));
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

static string ComputeSha256(string path)
{
    using var stream = File.OpenRead(path);
    var hash = SHA256.HashData(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ReleaseMetadataDocument))]
internal sealed partial class BundleMetadataJsonContext : JsonSerializerContext;
