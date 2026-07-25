using Microsoft.Extensions.Logging;

namespace Mimir.Server.Tests;

/// <summary>
/// A logger that keeps its warnings, for the services whose warning log is part of the behaviour
/// under test rather than background noise. Everything below Warning is dropped — a test that
/// asserted on Debug lines would pin phrasing, not behaviour.
/// </summary>
internal class CapturedLog : ILogger
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>
    /// Enabled for every level, and filtered on the capture side instead: a service that guards a
    /// log behind <c>IsEnabled</c> should still run that branch under test, exactly as it would
    /// against a real provider.
    /// </summary>
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
