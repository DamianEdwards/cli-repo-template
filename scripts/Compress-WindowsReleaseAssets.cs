#!/usr/bin/env dotnet

#:package System.CommandLine
#:property PublishAot=false

using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

var workingDirectoryOption = new Option<string>("--working-directory")
{
    Description = "Directory containing windows-assets-manifest.json and staged assets.",
    Required = true
};
var outputDirectoryOption = new Option<string>("--output-directory")
{
    Description = "Directory where signed Windows archives should be written.",
    Required = true
};

var command = new RootCommand("Repack expanded Windows release assets.");
command.Options.Add(workingDirectoryOption);
command.Options.Add(outputDirectoryOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var workingDirectory = Path.GetFullPath(parseResult.GetValue(workingDirectoryOption)!);
    var outputDirectory = Path.GetFullPath(parseResult.GetValue(outputDirectoryOption)!);
    var manifestPath = Path.Combine(workingDirectory, "windows-assets-manifest.json");

    EnsureFileExists(manifestPath, "Windows asset manifest");

    var manifestEntries = JsonSerializer.Deserialize(
        File.ReadAllText(manifestPath),
        WindowsAssetArchiveJsonContext.Default.ListWindowsAssetManifestEntry)
        ?? throw new InvalidOperationException($"Windows asset manifest '{manifestPath}' could not be parsed.");

    if (manifestEntries.Count == 0)
    {
        throw new InvalidOperationException($"Windows asset manifest '{manifestPath}' did not contain any entries.");
    }

    Directory.CreateDirectory(outputDirectory);

    foreach (var entry in manifestEntries)
    {
        var stagingDirectory = Path.GetFullPath(entry.StagingDirectory);
        EnsureDirectoryExists(stagingDirectory, "Staging directory");
        EnsureFileExists(Path.Combine(stagingDirectory, "templatecli.exe"), "Staging directory");
        EnsureFileExists(Path.Combine(stagingDirectory, "LICENSE"), "Staging directory");

        var archivePath = Path.Combine(outputDirectory, entry.AssetName);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        ZipFile.CreateFromDirectory(stagingDirectory, archivePath);
    }
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

internal sealed record WindowsAssetManifestEntry(
    string AssetName,
    string RuntimeIdentifier,
    string StagingDirectory);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<WindowsAssetManifestEntry>))]
internal sealed partial class WindowsAssetArchiveJsonContext : JsonSerializerContext;
