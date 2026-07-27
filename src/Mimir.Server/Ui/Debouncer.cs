using System.Diagnostics;

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
///
/// A trailing edge on its own postpones the run for as long as a burst keeps arriving, which is
/// exactly when a curator is watching a live row, so a caller may bound it — see
/// <paramref name="ceilingMultiple"/>.
/// </summary>
/// <param name="delay">How quiet the burst must go before the last scheduled action runs.</param>
/// <param name="logger">Where a failed action is reported — the caller cannot await it.</param>
/// <param name="what">Names the action in that log line.</param>
/// <param name="ceilingMultiple">
/// How many delays a burst may postpone the action for before it runs anyway, or null for the pure
/// trailing edge. With a ceiling the action runs at most <c>ceilingMultiple × delay</c> after the
/// burst's first signal however long the burst lasts, and the clock restarts from that run; the
/// trailing edge is unchanged, so an isolated signal still waits one delay either way. A multiple
/// rather than a duration because the tests shrink <paramref name="delay"/> to stay fast on real
/// timers, and an absolute ceiling would not shrink with it (#101, D6).
///
/// Feed-driven callers take <see cref="DefaultCeilingMultiple"/>; search-driven ones take none,
/// because a ceiling there fires mid-word and queries half a term — the thing their debounce
/// exists to prevent.
/// </param>
internal sealed class Debouncer(
    TimeSpan delay, ILogger logger, string what, int? ceilingMultiple = null) : IDisposable
{
    /// <summary>
    /// The wait every live surface debounces on, stated here rather than once per component — five
    /// copies of one constant was half of what #101 was filed about.
    /// </summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The ceiling the feed-driven surfaces take: four delays, so one second at
    /// <see cref="DefaultDelay"/>.
    ///
    /// The number rests on a structural argument rather than a measurement, and the argument is
    /// recorded here because the measurement is what will tempt the next reader to tighten it. The
    /// header and the project sidebar subscribe to the Episode feed *unfiltered* — they see the
    /// union of every Project's and every concurrent session's capture traffic merged, which is
    /// strictly denser than the single-session stream anybody profiles. Three of the header's four
    /// figures also have no publisher of their own, since nothing in Distillation, Harvest or
    /// Recall publishes, so incidental capture Events are their only refresh channel and
    /// withholding those for a burst's length is the whole reason this exists. Do not lower it on
    /// single-session numbers (#101, D9).
    /// </summary>
    public const int DefaultCeilingMultiple = 4;

    private readonly Lock _gate = new();

    /// <summary>How long a burst may postpone the run for, or null for no ceiling at all.</summary>
    private readonly TimeSpan? _ceiling = CeilingFrom(delay, ceilingMultiple);

    private CancellationTokenSource? _pending;

    /// <summary>
    /// When the burst in progress began, on the monotonic clock; null between bursts. Read and
    /// written only under <see cref="_gate"/> — the feed's publishes are concurrent.
    /// </summary>
    private long? _burstStartedAt;

    private bool _disposed;

    /// <summary>
    /// The ceiling this instance runs under, refusing a multiple that would quietly turn the keeper
    /// off: at zero or below every signal is already past its deadline, so the wait clamps
    /// to nothing, every signal runs immediately and the surface queries per keystroke with nothing
    /// in the log to say the debounce is gone.
    /// </summary>
    private static TimeSpan? CeilingFrom(TimeSpan delay, int? multiple)
    {
        if (multiple is not { } value)
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, "ceilingMultiple");
        return delay * value;
    }

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
            token = cts.Token;
            wait = NextWait();
        }

        // The source rides along beside its token for identity alone — the run compares it against
        // _pending to tell whether it is still the awaited one before clearing the burst clock. It
        // is never dereferenced there, which is what keeps the rule above intact: a superseding
        // Schedule may have disposed it by then, and a reference comparison does not care.
        _ = RunAsync(action, cts, token, wait);
    }

    /// <summary>
    /// How long this signal waits: a full delay, or whatever is left of the burst's ceiling if that
    /// runs out first. Called under <see cref="_gate"/>, which is where the burst clock has to live
    /// — the feed publishes on the capturing thread, so concurrent hook requests really do reach
    /// <see cref="Schedule"/> at once.
    /// </summary>
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

            // This burst has been served, so the next signal starts a new one and its ceiling is
            // measured from there rather than from a burst already paid for. Skipped when a later
            // signal superseded this run in the moment between the delay elapsing and the gate:
            // that signal's burst is the live one and its clock is not ours to clear. That signal
            // still runs — cancelling an elapsed delay is a no-op — so a ceiling makes two runs in
            // quick succession marginally likelier, which is what every caller's own generation
            // check is for (#101, D8).
            lock (_gate)
            {
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
