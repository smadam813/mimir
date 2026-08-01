using Microsoft.Extensions.Logging;

namespace Mimir.Server.Tests;

internal class CapturedLog : ILogger
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            Warnings.Add(formatter(state, exception));
        }
    }
}

/// <inheritdoc cref="CapturedLog"/>
internal sealed class CapturedLog<TCategory> : CapturedLog, ILogger<TCategory>;
