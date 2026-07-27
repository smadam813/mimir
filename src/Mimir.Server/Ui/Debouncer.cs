namespace Mimir.Server.Ui;

/// <summary>
/// The trailing-edge debounce the live surfaces schedule their refreshes through: a burst of
/// signals — Events off the feed, keystrokes in the header box — collapses to the last one, which
/// runs once the burst has been quiet for <c>delay</c>.
///
/// One copy because the shape is easy to get subtly wrong in each of them, and by #96 there were
/// four: the superseded token has to be cancelled *and* disposed, the cancellation has to be
/// swallowed rather than logged (it is the ordinary case, not a fault), everything else has to be
/// caught, because the caller abandons the returned task and an unobserved failure would otherwise
/// leave a surface silently stale — and the whole thing has to be safe against its own teardown,
/// which only <c>AppHeader</c>'s copy was. The Episode feed publishes on whichever thread captured,
/// not the circuit's dispatcher, so <see cref="Schedule"/> genuinely runs concurrently with
/// <see cref="Dispose"/>; unguarded, a signal landing mid-teardown installs a timer nobody will
/// ever cancel and it fires against a disposed component a delay later.
/// </summary>
/// <param name="delay">How quiet the burst must go before the last scheduled action runs.</param>
/// <param name="logger">Where a failed action is reported — the caller cannot await it.</param>
/// <param name="what">Names the action in that log line.</param>
internal sealed class Debouncer(TimeSpan delay, ILogger logger, string what) : IDisposable
{
    private readonly Lock _gate = new();

    private CancellationTokenSource? _pending;

    private bool _disposed;

    /// <summary>
    /// Schedules <paramref name="action"/>, superseding whatever was already pending, and does
    /// nothing at all once disposed. Returns as soon as the timer is armed — this is called from
    /// render and from feed callbacks, neither of which can await.
    /// </summary>
    public void Schedule(Func<Task> action)
    {
        CancellationTokenSource cts;
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
        }

        _ = RunAsync(action, cts.Token);
    }

    private async Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
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

    /// <summary>
    /// Drops anything pending, and closes the door behind it — a surface torn down mid-burst runs
    /// nothing after this returns, including from a signal racing it on another thread.
    /// </summary>
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
