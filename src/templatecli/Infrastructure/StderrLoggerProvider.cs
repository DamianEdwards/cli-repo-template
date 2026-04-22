using Microsoft.Extensions.Logging;

namespace TemplateCli.Infrastructure;

/// <summary>
/// Console logger provider that writes to stderr in grey text.
/// Only active when --log-level is specified.
/// </summary>
public sealed class StderrLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minLevel;

    public StderrLoggerProvider(LogLevel minLevel)
    {
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName, _minLevel);

    public void Dispose() { }
}

internal sealed class StderrLogger : ILogger
{
    private readonly string _category;
    private readonly LogLevel _minLevel;

    public StderrLogger(string category, LogLevel minLevel)
    {
        _category = category;
        _minLevel = minLevel;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss");
        var level = logLevel.ToString().ToUpperInvariant()[..4];

        // Write grey text to stderr
        Console.Error.Write("\x1b[90m");
        Console.Error.Write($"[{timestamp}] [{level}] {message}");
        if (exception is not null)
        {
            Console.Error.Write(Environment.NewLine);
            Console.Error.Write(exception);
        }
        Console.Error.WriteLine("\x1b[0m");
    }
}
