#!/usr/bin/env dotnet

#:package System.CommandLine
#:property PublishAot=false

using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

var directoryOption = new Option<string>("--directory")
{
    Description = "Directory containing install.ps1 and install.sh.",
    Required = true
};
var manifestVersionOption = new Option<string>("--manifest-version")
{
    Description = "Version string to write into the manifest.",
    Required = true
};
var sourceCommitOption = new Option<string>("--source-commit")
{
    Description = "Source commit SHA for the published scripts.",
    Required = true
};
var sourceRefOption = new Option<string>("--source-ref")
{
    Description = "Source ref for the published scripts."
};
sourceRefOption.DefaultValueFactory = _ => "refs/heads/main";

var command = new RootCommand("Generate install-script checksums and manifest metadata.");
command.Options.Add(directoryOption);
command.Options.Add(manifestVersionOption);
command.Options.Add(sourceCommitOption);
command.Options.Add(sourceRefOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var directory = Path.GetFullPath(parseResult.GetValue(directoryOption)!);
    var manifestVersion = parseResult.GetValue(manifestVersionOption)!;
    var sourceCommit = parseResult.GetValue(sourceCommitOption)!;
    var sourceRef = parseResult.GetValue(sourceRefOption)!;

    var installPs1Path = Path.Combine(directory, "install.ps1");
    var installShPath = Path.Combine(directory, "install.sh");

    EnsureFileExists(installPs1Path, "Install script");
    EnsureFileExists(installShPath, "Install script");

    var installPs1Sha = ComputeSha256(installPs1Path);
    var installShSha = ComputeSha256(installShPath);

    File.WriteAllLines(
        Path.Combine(directory, "checksums.txt"),
        [
            $"{installPs1Sha}  install.ps1",
            $"{installShSha}  install.sh"
        ]);

    var manifest = new InstallScriptsManifest(
        Version: manifestVersion,
        SourceCommit: sourceCommit,
        SourceRef: sourceRef,
        PublishedAt: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        Scripts:
        [
            new InstallScriptEntry("install.ps1", installPs1Sha),
            new InstallScriptEntry("install.sh", installShSha)
        ]);

    File.WriteAllText(
        Path.Combine(directory, "install-scripts.json"),
        JsonSerializer.Serialize(manifest, jsonOptions));
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
    catch (IOException ex)
    {
        Fail(ex.Message);
    }
    catch (JsonException ex)
    {
        Fail($"Invalid JSON input: {ex.Message}");
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

internal sealed record InstallScriptsManifest(
    string Version,
    string SourceCommit,
    string SourceRef,
    string PublishedAt,
    IReadOnlyList<InstallScriptEntry> Scripts);

internal sealed record InstallScriptEntry(string Name, string Sha256);
