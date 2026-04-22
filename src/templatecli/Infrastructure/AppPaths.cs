namespace TemplateCli.Infrastructure;

public static class AppPaths
{
    public static string GetUserProfileDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string GetAppHomeDirectory()
    {
        var configuredHome = Environment.GetEnvironmentVariable(AppIdentity.HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(configuredHome))
            return Path.GetFullPath(ExpandUserProfile(configuredHome));

        return Path.Combine(GetUserProfileDirectory(), $".{AppIdentity.CommandName}");
    }

    public static string ExpandUserProfile(string path)
    {
        if (!path.StartsWith('~'))
            return path;

        return Path.Combine(
            GetUserProfileDirectory(),
            path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public static string GetConfigDirDescription()
        => Environment.GetEnvironmentVariable(AppIdentity.HomeEnvVar) is { Length: > 0 }
            ? $"{GetAppHomeDirectory()} (from {AppIdentity.HomeEnvVar})"
            : GetAppHomeDirectory();

    public static string GetConfigPath()
        => Path.Combine(GetAppHomeDirectory(), "config.json");

    public static string GetLogsDirectory()
        => Path.Combine(GetAppHomeDirectory(), "logs");
}
