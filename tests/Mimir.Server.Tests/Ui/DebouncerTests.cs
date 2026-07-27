using Microsoft.Extensions.Logging.Abstractions;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The trailing-edge debounce the header and the sidebar schedule their refreshes through. Real
/// timers rather than a fake clock: what is being pinned is that a superseded run is *cancelled*,
/// which a <see cref="TimeProvider"/> seam would only pin against itself — <see cref="Task.Delay"/>
/// is what the production path actually waits on. The margins are an order of magnitude wide so a
/// loaded CI box cannot make them flap.
/// </summary>
public class DebouncerTests
{
    /// <summary>Short enough to keep the suite quick, long enough that a burst really is one.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(30);

    /// <summary>Ten times the delay — a run that has not happened by now was never going to.</summary>
    private static readonly TimeSpan LongEnough = TimeSpan.FromMilliseconds(300);

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
    public async Task DisposingMidBurst_RunsNothingAfterTheSurfaceIsGone()
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

    /// <summary>Waits long enough that a run which has not happened was never going to.</summary>
    private static Task Settle() => Task.Delay(LongEnough, TestContext.Current.CancellationToken);

    private static Debouncer New() => new(Delay, NullLogger.Instance, "test");
}
