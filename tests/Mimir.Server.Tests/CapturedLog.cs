using Microsoft.Extensions.Logging;

namespace Mimir.Server.Tests;

internal class CapturedLog : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<string> _warnings = [];

    /// <summary>A snapshot under the lock, not the list itself: a hosted service's loop logs from
    /// its own thread while the test reads this, and a bare <see cref="List{T}"/> is not safe
    /// across that. Nothing pins it — removing the lock leaves a race, not a failure.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return [.. _warnings];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel != LogLevel.Warning)
        {
            return;
        }

        var message = formatter(state, exception);
        lock (_gate)
        {
            _warnings.Add(message);
        }
    }
}

/// <inheritdoc cref="CapturedLog"/>
internal sealed class CapturedLog<TCategory> : CapturedLog, ILogger<TCategory>;
