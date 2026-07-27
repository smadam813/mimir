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
    ///
    /// The token is read while the gate is still held, not from the source afterwards. The thread
    /// the feed publishes on is not the circuit's, as above, so this runs concurrently with itself
    /// as well as with <see cref="Dispose"/> — two sessions capturing at once are two threads in
    /// here. Read after the release and the racer can have superseded — cancelled *and* disposed —
    /// the source this call just installed, and <see cref="CancellationTokenSource.Token"/> throws
    /// <see cref="ObjectDisposedException"/> on a disposed source. On the dispatcher that throw
    /// reaches a Blazor event handler and ends the circuit. Nothing can interpose on the window to
    /// pin it (#112 measured six escapes in 800,000 racing calls), so this is defense in depth
    /// rather than a mechanism any test holds in place.
    /// </summary>
    public void Schedule(Func<Task> action)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }

        _ = RunAsync(action, token);
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
    /// Drops anything still waiting out its delay, and closes the door behind it: a signal racing
    /// this on another thread is refused rather than arming a timer nobody will cancel.
    ///
    /// What it does not do is interrupt a run whose delay has already elapsed. Cancellation reaches
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and nothing past it — the scheduled
    /// action takes no token — and the run task is discarded, so there is nothing to await either.
    /// A surface torn down in that window still sees its last refresh finish.
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
