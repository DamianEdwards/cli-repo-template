namespace TemplateCli.Infrastructure;

public sealed class RuntimeContext
{
    public RuntimeContext()
    {
        ProcessPath = Environment.ProcessPath;
        SourceRoot = FindSourceRoot();
        SourceProjectPath = SourceRoot is null ? null : Path.Combine(SourceRoot, "src", AppIdentity.ProjectName);
    }

    public string? ProcessPath { get; }

    public string? SourceRoot { get; }

    public string? SourceProjectPath { get; }

    public bool IsDotnetHosted =>
        string.Equals(Path.GetFileName(ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public bool IsSourceTreeRun => SourceProjectPath is not null;

    public bool SupportsInPlaceSelfUpdate()
        => !IsDotnetHosted && !string.IsNullOrEmpty(ProcessPath);

    public string? GetUnsupportedSelfUpdateReason()
        => IsDotnetHosted
            ? $"Self-update is not supported when running {AppIdentity.CommandName} via dotnet run. Publish or install a {AppIdentity.CommandName} binary to test updates."
            : null;

    public CommandInvocation? GetSelfInvocation(string arguments)
    {
        if (IsDotnetHosted && !string.IsNullOrEmpty(SourceProjectPath))
        {
            var prefix = $"run --project \"{SourceProjectPath}\" --";
            var resolvedArguments = string.IsNullOrWhiteSpace(arguments)
                ? prefix
                : $"{prefix} {arguments}";
            return new CommandInvocation(ProcessPath ?? "dotnet", resolvedArguments);
        }

        if (string.IsNullOrEmpty(ProcessPath))
            return null;

        return new CommandInvocation(ProcessPath, arguments);
    }

    private static string? FindSourceRoot()
    {
        foreach (var candidate in GetSearchRoots())
        {
            for (var dir = candidate; dir is not null; dir = Directory.GetParent(dir)?.FullName)
            {
                if (LooksLikeSourceRoot(dir))
                    return dir;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            var processDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(processDir))
                yield return processDir;
        }
    }

    private static bool LooksLikeSourceRoot(string path)
    {
        var projectPath = Path.Combine(path, "src", AppIdentity.ProjectName, $"{AppIdentity.ProjectName}.csproj");
        if (!File.Exists(projectPath))
            return false;

        return File.Exists(Path.Combine(path, $"{AppIdentity.CommandName}.cmd"))
            || File.Exists(Path.Combine(path, $"{AppIdentity.CommandName}.sh"));
    }
}

public sealed record CommandInvocation(string FileName, string Arguments)
{
    public string GetCommandLine()
        => string.IsNullOrWhiteSpace(Arguments)
            ? $"\"{FileName}\""
            : $"\"{FileName}\" {Arguments}";
}
