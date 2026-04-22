using Spectre.Console;
using Microsoft.Extensions.Logging;

namespace TemplateCli.Infrastructure;

/// <summary>
/// Wraps all user-facing console output through Spectre.Console.
/// Ensures errors are rendered in red, warnings in yellow, and success in green.
/// Prevents raw exceptions from reaching the user.
/// </summary>
public static class ConsoleOutput
{
    public static void Success(string message)
        => AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");

    public static void Info(string message)
        => AnsiConsole.MarkupLine(Markup.Escape(message));

    public static void Warning(string message)
        => AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");

    public static void Error(string message)
        => AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    public static void Verbose(string message)
        => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    /// <summary>
    /// Runs an action with a centralized error boundary. Returns the exit code.
    /// </summary>
    public static async Task<int> RunWithErrorHandling(Func<Task<int>> action, ILogger? logger = null)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            Warning("Operation cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unhandled exception");
            Error($"Error: {ex.Message}");
            return 1;
        }
    }
}
