using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TemplateCli.Infrastructure;

namespace TemplateCli.Commands;

public static class ConfigCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("config", $"Manage {AppIdentity.CommandName} configuration");

        var setOption = new Option<string?>("--set")
        {
            Description = "Set a config value in key=value format (for example: default_log_level=debug)"
        };
        command.Options.Add(setOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ConfigCommand));
            return await ConsoleOutput.RunWithErrorHandling(() =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var setValue = parseResult.GetValue(setOption);

                if (string.IsNullOrWhiteSpace(setValue))
                {
                    ShowConfig(stateStore);
                    return Task.FromResult(0);
                }

                var eqIdx = setValue.IndexOf('=');
                if (eqIdx <= 0)
                {
                    ConsoleOutput.Error("Invalid format. Use --set key=value");
                    return Task.FromResult(1);
                }

                var key = setValue[..eqIdx].Trim().ToLowerInvariant();
                var value = setValue[(eqIdx + 1)..].Trim();
                var config = stateStore.LoadConfig();

                switch (key)
                {
                    case "default_log_level":
                    case "log_level":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            config.DefaultLogLevel = null;
                            ConsoleOutput.Success("default_log_level cleared.");
                            break;
                        }

                        if (!LogLevelParser.TryParse(value, out _))
                        {
                            ConsoleOutput.Error("default_log_level must be one of: debug, information, warning, error");
                            return Task.FromResult(1);
                        }

                        config.DefaultLogLevel = LogLevelParser.Normalize(value);
                        ConsoleOutput.Success($"default_log_level set to: {config.DefaultLogLevel}");
                        break;

                    case "include_prerelease_updates":
                    case "prerelease_updates":
                    case "pre_release_updates":
                        if (!TryParseBoolean(value, out var includePrereleaseUpdates))
                        {
                            ConsoleOutput.Error("include_prerelease_updates must be true or false");
                            return Task.FromResult(1);
                        }

                        config.IncludePrereleaseUpdates = includePrereleaseUpdates;
                        ConsoleOutput.Success($"include_prerelease_updates set to: {config.IncludePrereleaseUpdates.ToString().ToLowerInvariant()}");
                        break;

                    default:
                        ConsoleOutput.Error($"Unknown config key: {key}");
                        ConsoleOutput.Info("Valid keys: default_log_level, include_prerelease_updates");
                        return Task.FromResult(1);
                }

                stateStore.SaveConfig(config);
                ConsoleOutput.Info($"Config file: {stateStore.ConfigPath}");
                return Task.FromResult(0);
            }, logger);
        });

        return command;
    }

    private static void ShowConfig(StateStore stateStore)
    {
        var config = stateStore.LoadConfig();

        ConsoleOutput.Info($"Config file: {stateStore.ConfigPath}");
        if (!stateStore.ConfigExists())
            ConsoleOutput.Info("No config file found; showing defaults.");

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.ShowRowSeparators = true;
        table.AddColumn(new TableColumn("[bold]Key[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Value[/]"));
        table.AddRow("schema_version", Markup.Escape(config.SchemaVersion.ToString()));
        table.AddRow("default_log_level", Markup.Escape(config.DefaultLogLevel ?? "(not set)"));
        table.AddRow("include_prerelease_updates", Markup.Escape(config.IncludePrereleaseUpdates.ToString().ToLowerInvariant()));

        AnsiConsole.Write(table);
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
