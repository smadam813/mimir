using System.Diagnostics;

namespace Mimir.Server.Ui;

internal sealed class Debouncer(
    TimeSpan delay, ILogger logger, string what, int? ceilingMultiple = null) : IDisposable
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    public const int DefaultCeilingMultiple = 4;

    private readonly Lock _gate = new();

    private readonly TimeSpan? _ceiling = CeilingFrom(delay, ceilingMultiple);

    private CancellationTokenSource? _pending;

    // Read and written only under _gate.
    private long? _burstStartedAt;

    private bool _disposed;

    private static TimeSpan? CeilingFrom(TimeSpan delay, int? multiple)
    {
        if (multiple is not { } value)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, "ceilingMultiple");
        return delay * value;
    }

    public void Schedule(Func<Task> action)
    {
        CancellationTokenSource cts;
        CancellationToken token;
        TimeSpan wait;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending?.Cancel();
            _pending?.Dispose();
            cts = new CancellationTokenSource();
            _pending = cts;
            // Inside the gate: a racer can dispose this source, and Token throws on a disposed one.
            token = cts.Token;
            wait = NextWait();
        }

        // The source rides along for identity alone and is never dereferenced in the run.
        _ = RunAsync(action, cts, token, wait);
    }

    // Called under _gate, which is where the burst clock lives.
    private TimeSpan NextWait()
    {
        if (_ceiling is not { } ceiling)
        {
            return delay;
        }

        _burstStartedAt ??= Stopwatch.GetTimestamp();
        var left = ceiling - Stopwatch.GetElapsedTime(_burstStartedAt.Value);
        if (left <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return left < delay ? left : delay;
    }

    private async Task RunAsync(
        Func<Task> action,
        CancellationTokenSource pending,
        CancellationToken cancellationToken,
        TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait, cancellationToken);

            lock (_gate)
            {
                // A signal that superseded this run owns the burst clock; it is not ours to clear.
                if (_pending == pending)
                {
                    _burstStartedAt = null;
                }
            }

            await action();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later signal before the delay elapsed, or disposed — both ordinary.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{What} failed", what);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
