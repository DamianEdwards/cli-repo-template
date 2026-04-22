namespace TemplateCli.Models;

/// <summary>
/// Top-level user-managed configuration persisted under the CLI home directory
/// (defaults to ~/.templatecli/config.json, overrideable with TEMPLATECLI_HOME).
/// </summary>
public sealed class TemplateCliConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Default console log level used when --log-level is not specified.
    /// Supported values: debug, information, warning, error.
    /// </summary>
    public string? DefaultLogLevel { get; set; }

    /// <summary>
    /// Whether update checks should include pre-release versions by default.
    /// </summary>
    public bool IncludePrereleaseUpdates { get; set; }
}
