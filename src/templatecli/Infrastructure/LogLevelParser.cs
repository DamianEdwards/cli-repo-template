using Microsoft.Extensions.Logging;

namespace TemplateCli.Infrastructure;

internal static class LogLevelParser
{
    public static bool TryParse(string? value, out LogLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "debug":
            case "trace":
                level = LogLevel.Debug;
                return true;
            case "info":
            case "information":
                level = LogLevel.Information;
                return true;
            case "warn":
            case "warning":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            default:
                level = default;
                return false;
        }
    }

    public static string Normalize(string value)
        => TryParse(value, out var level)
            ? level switch
            {
                LogLevel.Debug => "debug",
                LogLevel.Information => "information",
                LogLevel.Warning => "warning",
                LogLevel.Error => "error",
                _ => "information",
            }
            : throw new ArgumentException($"Unsupported log level: {value}", nameof(value));
}
