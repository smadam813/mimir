using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The trailing-edge debounce the live surfaces schedule their refreshes through. Real
/// timers rather than a fake clock: what is being pinned is that a superseded run is *cancelled*,
/// which a <see cref="TimeProvider"/> seam would only pin against itself — <see cref="Task.Delay"/>
/// is what the production path actually waits on.
///
/// The margins are stated per test rather than claimed across the board, because the two ceiling
/// tests are not the same shape as the other six. Those six wait <see cref="LongEnough"/> — ten
/// times <see cref="Delay"/> — for something that either happened or never will, and a slow box
/// only makes them safer. The ceiling pair is the first whose correctness needs a gap to stay
/// *under* a delay: its burst signals every <see cref="BurstGap"/> against a
/// <see cref="CeilingDelay"/> ten times longer, and a box slow enough to stretch a 10 ms timer past
/// 100 ms would let the trailing edge elapse mid-burst and break both of them. Nominally 10×; on
/// Windows, whose timer granularity rounds that gap up to about 16 ms, nearer 6×. That is why the
/// pair uses its own longer delay instead of the 30 ms the rest run on.
/// </summary>
public class DebouncerTests
{
    /// <summary>Short enough to keep the suite quick, long enough that a burst really is one.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(30);

    /// <summary>Ten times the delay — a run that has not happened by now was never going to.</summary>
    private static readonly TimeSpan LongEnough = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The ceiling pair's delay. Longer than <see cref="Delay"/> so <see cref="BurstGap"/> sits a
    /// clear order of magnitude inside it — see the class comment on why this pair is the one whose
    /// margin a slow box eats.
    /// </summary>
    private static readonly TimeSpan CeilingDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The gap between a burst's signals: well under the delay, so a pure trailing edge
    /// never elapses while the burst lasts.</summary>
    private static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task ABurst_RunsOnceWithTheLastSignalsWork_NotOncePerSignal()
    {
        var ran = new List<int>();
        using var debouncer = New();

        for (var i = 1; i <= 5; i++)
        {
            var signal = i;
            debouncer.Schedule(() =>
            {
                ran.Add(signal);
                return Task.CompletedTask;
            });
        }

        await Settle();

        ran.ShouldBe([5]);
    }

    [Fact]
    public async Task AQuietGap_LetsTheNextSignalRunOnItsOwn()
    {
        // The other half: it debounces rather than throttles, so two signals far enough apart are
        // two runs. Without this a "runs once" pin passes just as well on a Schedule that drops
        // everything after the first.
        var ran = 0;
        using var debouncer = New();

        debouncer.Schedule(() => { ran++; return Task.CompletedTask; });
        await Settle();
        debouncer.Schedule(() => { ran++; return Task.CompletedTask; });
        await Settle();

        ran.ShouldBe(2);
    }

    [Fact]
    public async Task DisposingMidBurst_RunsNothingThatWasStillWaitingOutItsDelay()
    {
        // A component torn down inside the window would otherwise touch its own disposed state
        // — the refresh writes fields and calls StateHasChanged on a circuit that has ended.
        var ran = 0;
        var debouncer = New();
        debouncer.Schedule(() => { ran++; return Task.CompletedTask; });

        debouncer.Dispose();
        await Settle();

        ran.ShouldBe(0);
    }

    [Fact]
    public async Task AFailingAction_IsReported_NotLeftForNobodyToObserve()
    {
        // Scheduled fire-and-forget from a feed or a keystroke, so an escaping exception is
        // nobody's to catch: it would surface as an unobserved task exception at some later GC,
        // leaving a transient Postgres failure as a silently stale surface.
        //
        // Asserted on the log rather than on "the next signal still ran", which is true either
        // way — an abandoned task's exception does not stop the next Schedule, so that pin would
        // stay green with the catch deleted.
        var log = new CapturedLog();
        using var debouncer = new Debouncer(Delay, log, "Header pipeline refresh");

        debouncer.Schedule(() => throw new InvalidOperationException("the database went away"));
        await Settle();

        log.Warnings.ShouldHaveSingleItem().ShouldBe("Header pipeline refresh failed");
    }

    [Fact]
    public async Task ASupersededRun_IsNotReportedAsAFailure()
    {
        // The other side of that log: cancelling is how this thing works, so a superseded run
        // logging a warning would fill the log with one line per keystroke.
        var log = new CapturedLog();
        using var debouncer = new Debouncer(Delay, log, "Surface search");

        debouncer.Schedule(() => Task.CompletedTask);
        debouncer.Schedule(() => Task.CompletedTask);
        await Settle();

        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASignalRacingDispose_IsRefusedRatherThanArmingATimerNobodyCancels()
    {
        // The Episode feed publishes on whichever thread captured, not the circuit's dispatcher,
        // so this really is concurrent. Unguarded, a Schedule landing just after Dispose installs
        // a fresh token that nothing holds a reference to — it fires a delay later against a
        // component that has already gone.
        var ran = 0;
        var debouncer = New();

        await Task.WhenAll(
            Task.Run(debouncer.Dispose, TestContext.Current.CancellationToken),
            Task.Run(
                () =>
                {
                    for (var i = 0; i < 200; i++)
                    {
                        debouncer.Schedule(() => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
                    }
                },
                TestContext.Current.CancellationToken));
        await Settle();

        // Whatever was scheduled before Dispose won the race was still inside its delay, so it was
        // cancelled by it; whatever came after was refused. Either way nothing survives here — a
        // run whose delay had already elapsed would not have been, but the loop is far too fast to
        // produce one.
        Volatile.Read(ref ran).ShouldBe(0);
    }

    [Fact]
    public async Task ABurstPastTheCeiling_RunsDuringIt_NotOnlyOnceItGoesQuiet()
    {
        // The starvation a trailing edge alone has: capture keeps publishing, so the refresh keeps
        // being postponed, and the curator watching a live row sees nothing for the whole burst.
        var ran = 0;
        using var debouncer = new Debouncer(
            CeilingDelay, NullLogger.Instance, "test", Debouncer.DefaultCeilingMultiple);

        await BurstAsync(debouncer, () => { Interlocked.Increment(ref ran); return Task.CompletedTask; });

        // Asserted before the burst is allowed to go quiet, and bounded on both sides. The burst
        // spans two and a half ceilings, so at least two runs is what "keeps measuring after the
        // first one" looks like; at most four is what keeps it a debounce. A ceiling whose clock
        // never restarted would leave every later signal already past the deadline and fire on
        // each one — dozens of runs, and a bare "ran at all" pin would call that a pass.
        Volatile.Read(ref ran).ShouldBeInRange(2, 4);
    }

    [Fact]
    public async Task ThatSameBurst_WithNoCeilingConfigured_RunsNothingUntilItGoesQuiet()
    {
        // The three search-driven instances configure no ceiling, because one there would fire
        // mid-word and query half a term. That exclusion is a behaviour, so it is pinned rather
        // than left as an absence — and it is what makes the ceiling test above a real pin.
        var ran = 0;
        using var debouncer = new Debouncer(CeilingDelay, NullLogger.Instance, "test");

        await BurstAsync(debouncer, () => { Interlocked.Increment(ref ran); return Task.CompletedTask; });

        Volatile.Read(ref ran).ShouldBe(0);

        // And then the trailing edge still arrives — without this, a Schedule that dropped every
        // signal on the floor would pass the assertion above.
        await Task.Delay(CeilingDelay * 3, TestContext.Current.CancellationToken);
        Volatile.Read(ref ran).ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACeilingOfZeroOrLess_IsRefused_RatherThanTurningTheDebounceOff(int multiple)
    {
        // Not a tidiness guard. A ceiling of zero leaves every signal already past its deadline, so
        // the wait clamps to nothing and each one runs immediately: the surface queries per
        // keystroke, and because the keeper swallows nothing and logs nothing on that path, the
        // only evidence is the load. Cheap to make loud at construction instead.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new Debouncer(Delay, NullLogger.Instance, "test", multiple));
    }

    /// <summary>
    /// Signals every <see cref="BurstGap"/> for two and a half ceilings' worth of wall clock —
    /// gaps far inside <see cref="CeilingDelay"/>, so the trailing edge never elapses and only a
    /// ceiling can run anything. Bounded on the clock rather than on a signal count, so a box whose
    /// timers round 10 ms up still spans the ceiling rather than merely sending more signals.
    /// </summary>
    private static async Task BurstAsync(Debouncer debouncer, Func<Task> action)
    {
        var length = CeilingDelay * Debouncer.DefaultCeilingMultiple * 2.5;
        var burst = Stopwatch.StartNew();
        while (burst.Elapsed < length)
        {
            debouncer.Schedule(action);
            await Task.Delay(BurstGap, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits long enough that a run which has not happened was never going to.</summary>
    private static Task Settle() => Task.Delay(LongEnough, TestContext.Current.CancellationToken);

    private static Debouncer New() => new(Delay, NullLogger.Instance, "test");
}
