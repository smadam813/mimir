using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

public class DebouncerTests
{
    // Short enough to keep the suite quick, long enough that a burst really is one.
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(30);

    // Ten times the delay: a run that has not happened by now was never going to.
    private static readonly TimeSpan LongEnough = TimeSpan.FromMilliseconds(300);

    // The ceiling pair's own delay: its margin is the one a slow box eats.
    private static readonly TimeSpan CeilingDelay = TimeSpan.FromMilliseconds(100);

    // The gap between a burst's signals: well under the delay, so a pure trailing edge never
    // elapses while the burst lasts.
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
        var log = new CapturedLog();
        using var debouncer = new Debouncer(Delay, log, "Header pipeline refresh");

        debouncer.Schedule(() => throw new InvalidOperationException("the database went away"));
        await Settle();

        log.Warnings.ShouldHaveSingleItem().ShouldBe("Header pipeline refresh failed");
    }

    [Fact]
    public async Task ASupersededRun_IsNotReportedAsAFailure()
    {
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

        Volatile.Read(ref ran).ShouldBe(0);
    }

    [Fact]
    public async Task ABurstPastTheCeiling_RunsDuringIt_NotOnlyOnceItGoesQuiet()
    {
        var ran = 0;
        using var debouncer = new Debouncer(
            CeilingDelay, NullLogger.Instance, "test", Debouncer.DefaultCeilingMultiple);

        await BurstAsync(debouncer, () => { Interlocked.Increment(ref ran); return Task.CompletedTask; });

        Volatile.Read(ref ran).ShouldBeInRange(2, 4);
    }

    [Fact]
    public async Task ThatSameBurst_WithNoCeilingConfigured_RunsNothingUntilItGoesQuiet()
    {
        var ran = 0;
        using var debouncer = new Debouncer(CeilingDelay, NullLogger.Instance, "test");

        await BurstAsync(debouncer, () => { Interlocked.Increment(ref ran); return Task.CompletedTask; });

        Volatile.Read(ref ran).ShouldBe(0);

        await Task.Delay(CeilingDelay * 3, TestContext.Current.CancellationToken);
        Volatile.Read(ref ran).ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACeilingOfZeroOrLess_IsRefused_RatherThanTurningTheDebounceOff(int multiple)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new Debouncer(Delay, NullLogger.Instance, "test", multiple));
    }

    private static async Task BurstAsync(Debouncer debouncer, Func<Task> action)
    {
        // Bounded on the clock, not on a signal count, so a box with coarse timers still spans it.
        var length = CeilingDelay * Debouncer.DefaultCeilingMultiple * 2.5;
        var burst = Stopwatch.StartNew();
        while (burst.Elapsed < length)
        {
            debouncer.Schedule(action);
            await Task.Delay(BurstGap, TestContext.Current.CancellationToken);
        }
    }

    private static Task Settle() => Task.Delay(LongEnough, TestContext.Current.CancellationToken);

    private static Debouncer New() => new(Delay, NullLogger.Instance, "test");
}
