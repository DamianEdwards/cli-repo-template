using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TemplateCli.Infrastructure;

namespace TemplateCli.Commands;

public static class LogsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("logs", "Show or clear rolled log files");
        command.Aliases.Add("log");
        command.Subcommands.Add(CreateClearCommand(services));

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LogsCommand));
            return await ConsoleOutput.RunWithErrorHandling(() =>
            {
                var logFileManager = services.GetRequiredService<LogFileManager>();
                ConsoleOutput.Info($"Logs: {logFileManager.GetLogsRootDirectoryForDisplay()}");
                ConsoleOutput.Info("Log files roll automatically by date and size.");
                return Task.FromResult(0);
            }, logger);
        });

        return command;
    }

    private static Command CreateClearCommand(IServiceProvider services)
    {
        var command = new Command("clear", "Clear rolled log files");
        var daysOption = new Option<int?>("--days")
        {
            Description = "Delete log files older than this many days"
        };
        command.Options.Add(daysOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LogsCommand));
            return await ConsoleOutput.RunWithErrorHandling(() =>
            {
                var logFileManager = services.GetRequiredService<LogFileManager>();
                var days = parseResult.GetValue(daysOption);
                if (days is { } age && age <= 0)
                {
                    ConsoleOutput.Error("--days must be a positive integer.");
                    return Task.FromResult(1);
                }

                if (days is null && !AnsiConsole.Confirm("Clear all log files except the active log file?", false))
                {
                    ConsoleOutput.Warning("Log clear cancelled.");
                    return Task.FromResult(0);
                }

                var clearResult = logFileManager.ClearLogs(days);
                foreach (var warning in clearResult.Warnings)
                    ConsoleOutput.Warning(warning);

                ConsoleOutput.Success(days is { } keepAge
                    ? $"Cleared {clearResult.DeletedCount} log file(s) older than {keepAge} day(s)."
                    : $"Cleared {clearResult.DeletedCount} log file(s).");
                ConsoleOutput.Info($"Logs: {logFileManager.GetLogsRootDirectoryForDisplay()}");
                return Task.FromResult(0);
            }, logger);
        });

        return command;
    }
}
