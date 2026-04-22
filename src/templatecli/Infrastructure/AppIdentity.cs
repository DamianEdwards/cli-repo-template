namespace TemplateCli.Infrastructure;

public static class AppIdentity
{
    public const string ProductName = "Template CLI";
    public const string CommandName = "templatecli";
    public const string ProjectName = "templatecli";
    public const string DefaultRepository = "example/templatecli";
    public const string HomeEnvVar = "TEMPLATECLI_HOME";
    public const string DisableSelfUpdatesEnvVar = "TEMPLATECLI_DISABLE_SELF_UPDATES";
    public const string UpdateSourceEnvVar = "TEMPLATECLI_UPDATE_SOURCE";
    public const string UpdateRepositoryEnvVar = "TEMPLATECLI_UPDATE_REPOSITORY";

    public static string GetExecutableFileName()
        => OperatingSystem.IsWindows() ? $"{CommandName}.exe" : CommandName;
}
