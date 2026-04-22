namespace TemplateCli.Infrastructure;

public sealed class LogFileManager
{
    private const long MaxFileSize = 10 * 1024 * 1024;
    private readonly string _rootLogDirectory;

    public LogFileManager()
    {
        _rootLogDirectory = AppPaths.GetLogsDirectory();
        Directory.CreateDirectory(_rootLogDirectory);
    }

    public string RootLogDirectory => _rootLogDirectory;

    public string GetLogsRootDirectoryForDisplay() => NormalizePath(_rootLogDirectory);

    public string GetActiveLogFilePath()
    {
        Directory.CreateDirectory(_rootLogDirectory);
        return GetLogFilePath(_rootLogDirectory);
    }

    public LogClearResult ClearLogs(int? days)
    {
        var deleted = 0;
        var warnings = new List<string>();
        var cutoff = days is { } age ? DateTimeOffset.UtcNow.AddDays(-age) : (DateTimeOffset?)null;
        var activeLogFile = GetActiveLogFilePath();

        foreach (var logFile in EnumerateLogFiles())
        {
            try
            {
                if (PathsEqual(logFile, activeLogFile))
                    continue;

                var lastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(logFile), TimeSpan.Zero);
                if (cutoff is { } cutoffTime && lastWriteTime >= cutoffTime)
                    continue;

                File.Delete(logFile);
                deleted++;
            }
            catch (IOException ex)
            {
                warnings.Add($"Failed to clear log file '{NormalizePath(logFile)}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                warnings.Add($"Failed to clear log file '{NormalizePath(logFile)}': {ex.Message}");
            }
        }

        return new LogClearResult(deleted, warnings);
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        if (!Directory.Exists(_rootLogDirectory))
            yield break;

        foreach (var logFile in Directory.EnumerateFiles(_rootLogDirectory, "*.log", SearchOption.TopDirectoryOnly))
            yield return logFile;
    }

    private static string GetLogFilePath(string logDirectory)
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var basePath = Path.Combine(logDirectory, $"{AppIdentity.CommandName}-{date}.log");

        if (File.Exists(basePath))
        {
            var info = new FileInfo(basePath);
            if (info.Length >= MaxFileSize)
            {
                for (var i = 1; ; i++)
                {
                    var rolledPath = Path.Combine(logDirectory, $"{AppIdentity.CommandName}-{date}-{i}.log");
                    if (!File.Exists(rolledPath) || new FileInfo(rolledPath).Length < MaxFileSize)
                        return rolledPath;
                }
            }
        }

        return basePath;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizeFullPath(left), NormalizeFullPath(right), PathComparison);

    private static string NormalizeFullPath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizePath(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed record LogClearResult(int DeletedCount, IReadOnlyList<string> Warnings);
