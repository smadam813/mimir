namespace Mimir.Server.Ui;

/// <summary>
/// The trailing-edge debounce the live surfaces schedule their refreshes through: a burst of
/// signals — Events off the feed, keystrokes in the header box — collapses to the last one, which
/// runs once the burst has been quiet for <c>delay</c>.
///
/// One copy because the shape is easy to get subtly wrong in each of them: the superseded token has
/// to be cancelled *and* disposed, the cancellation has to be swallowed rather than logged (it is
/// the ordinary case, not a fault), and everything else has to be caught, because the caller
/// abandons the returned task and an unobserved failure would otherwise leave a surface silently
/// stale. Owning the token here also means a component holding two of these disposes two objects
/// rather than remembering to cancel-and-dispose four fields by hand.
/// </summary>
/// <param name="delay">How quiet the burst must go before the last scheduled action runs.</param>
/// <param name="logger">Where a failed action is reported — the caller cannot await it.</param>
/// <param name="what">Names the action in that log line.</param>
internal sealed class Debouncer(TimeSpan delay, ILogger logger, string what) : IDisposable
{
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Schedules <paramref name="action"/>, superseding whatever was already pending. Returns as
    /// soon as the timer is armed — this is called from render and from feed callbacks, neither of
    /// which can await.
    /// </summary>
    public void Schedule(Func<Task> action)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;
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

    /// <summary>Drops anything pending — a surface torn down mid-burst runs nothing after it.</summary>
    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
