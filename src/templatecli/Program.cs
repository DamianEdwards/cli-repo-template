using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemplateCli.Commands;
using TemplateCli.Infrastructure;
using TemplateCli.Models;
using TemplateCli.Services;

namespace TemplateCli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await ConsoleOutput.RunWithErrorHandling(async () =>
        {
            var bootstrapConfig = StateStore.LoadBootstrapConfig();
            var consoleLogLevel = ParseLogLevel(args, bootstrapConfig);
            var services = ConfigureServices(consoleLogLevel);
            var runtimeContext = services.GetRequiredService<RuntimeContext>();
            var stateStore = services.GetRequiredService<StateStore>();
            var updateService = services.GetRequiredService<UpdateService>();

            if (IsStartupRepairCommand(args) && stateStore.IsUpdateLockHeld())
            {
                ConsoleOutput.Error($"A self-update is already in progress. Wait for it to finish before starting {AppIdentity.CommandName}.");
                return 1;
            }

            if (ShouldRunStartupRepair(args, runtimeContext, stateStore)
                && await updateService.RepairInterruptedInstallAsync(skipProvenance: false, CancellationToken.None) is { } repairResult)
            {
                if (!repairResult.Succeeded)
                {
                    ConsoleOutput.Error(repairResult.Message);
                    return 1;
                }

                ConsoleOutput.Info(repairResult.Message);

                if (repairResult.RelaunchRequired)
                {
                    var relaunchExitCode = await RelaunchWithCurrentArgumentsAsync(args, CancellationToken.None);
                    if (relaunchExitCode is null)
                    {
                        ConsoleOutput.Error($"Startup repair installed a newer binary, but {AppIdentity.CommandName} could not relaunch the requested command automatically.");
                        return 1;
                    }

                    return relaunchExitCode.Value;
                }
            }

            var rootCommand = new RootCommand($"{AppIdentity.CommandName} - reusable .NET CLI template with self-update and release automation");
            rootCommand.Subcommands.Add(CompletionsCommand.Create());
            rootCommand.Subcommands.Add(ConfigCommand.Create(services));
            rootCommand.Subcommands.Add(LogsCommand.Create(services));
            rootCommand.Subcommands.Add(UpdateCommand.Create(services));
            rootCommand.Subcommands.Add(ShutdownInstanceCommand.Create(services));

            var parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync(new() { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) });
        });
    }

    private static ServiceProvider ConfigureServices(LogLevel? consoleLogLevel)
    {
        var services = new ServiceCollection();

        services.AddSingleton<LogFileManager>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<ILoggerProvider>(sp => new FileLoggerProvider(sp.GetRequiredService<LogFileManager>()));
        if (consoleLogLevel is { } level)
            services.AddSingleton<ILoggerProvider>(_ => new StderrLoggerProvider(level));

        services.AddSingleton<StateStore>();
        services.AddSingleton<GitHubReleaseService>();
        services.AddSingleton<ProvenanceVerifier>();
        services.AddSingleton<RuntimeContext>();
        services.AddSingleton<UpdateService>();

        return services.BuildServiceProvider();
    }

    private static LogLevel? ParseLogLevel(string[] args, TemplateCliConfig config)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--log-level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return ParseLogLevelValue(args[i + 1]);

            if (args[i].StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
                return ParseLogLevelValue(args[i]["--log-level=".Length..]);
        }

        if (LogLevelParser.TryParse(config.DefaultLogLevel, out var configuredLevel))
            return configuredLevel;

        return null;
    }

    private static LogLevel ParseLogLevelValue(string value)
        => LogLevelParser.TryParse(value, out var level)
            ? level
            : LogLevel.Information;

    private static bool ShouldRunStartupRepair(string[] args, RuntimeContext runtimeContext, StateStore stateStore)
        => runtimeContext.SupportsInPlaceSelfUpdate()
           && !stateStore.IsLockHeld()
           && IsStartupRepairCommand(args);

    private static bool IsStartupRepairCommand(string[] args)
    {
        if (args.Any(IsHelpOrVersionArgument))
            return false;

        var command = args.FirstOrDefault(arg => !arg.StartsWith('-'));
        return !IsDirectiveArgument(command)
               && !string.Equals(command, "shutdown-instance", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelpOrVersionArgument(string arg)
        => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "-?", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectiveArgument(string? arg)
        => !string.IsNullOrWhiteSpace(arg)
           && arg.StartsWith('[')
           && arg.EndsWith(']');

    private static async Task<int?> RelaunchWithCurrentArgumentsAsync(string[] args, CancellationToken ct)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return null;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return null;

            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }
}
