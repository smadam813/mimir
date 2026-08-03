using Microsoft.Extensions.Time.Testing;

namespace Mimir.Server.Tests;

/// <summary>
/// A <see cref="FakeTimeProvider"/> wrapped so it also says when the loop under test has
/// <em>parked</em> on it, which the fake alone cannot: it exposes no pending-timer count, so a test
/// advancing before the loop has registered its <c>Task.Delay</c> fires nothing and is told nothing
/// about why. The timer registered afterwards computes its due time from the already-advanced
/// clock, so the advance meant to cross the interval is simply lost and the wait after it times out.
/// A real-time pause is no fix — its correctness needs the registration to land <em>inside</em> the
/// pause, which is the shape that breaks on a loaded box. So this records each registration as it is
/// made, and <see cref="StraddleAsync"/> takes the one it is about to cross.
/// </summary>
internal sealed class LoopClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>Real time, in the safe direction of it: what this waits for is nothing happening,
    /// so a slow box only makes the check stronger. The other direction — a pause the loop has to
    /// get something done inside — is the one this class exists to remove.</summary>
    private static readonly TimeSpan Held = TimeSpan.FromMilliseconds(500);

    private readonly FakeTimeProvider _clock = new(now);
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _parks = [];

    private int _claimed;

    public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;

    public override long TimestampFrequency => _clock.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

    public override long GetTimestamp() => _clock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _clock.CreateTimer(callback, state, dueTime, period);
        // Recorded after the inner registration and never before it: what a waiter needs to know is
        // that the due time is already fixed against the clock as it stands.
        lock (_gate)
        {
            _parks.Add(dueTime);
        }

        return timer;
    }

    public void Advance(TimeSpan delta) => _clock.Advance(delta);

    /// <summary>
    /// Cross <paramref name="interval"/> in two advances, so a loop that waits <em>longer</em> than
    /// it fails as loudly as one that waits less: take the park and check it is owed to the
    /// interval under test, advance to one <paramref name="margin"/> short of it and hold there
    /// long enough to see nothing fire, then advance the margin and wait for
    /// <paramref name="fired"/>. Advancing repeatedly until something fires would pin nothing about
    /// the interval's length, since the total advance is unbounded.
    /// </summary>
    public async Task StraddleAsync(
        TimeSpan interval,
        TimeSpan margin,
        Func<bool> fired,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        var park = await NextParkAsync(patience, cancellationToken);
        park.ShouldBe(interval, "the loop's next pass is owed to the interval under test");

        Advance(interval - margin);
        await Task.Delay(Held, cancellationToken);
        fired().ShouldBeFalse($"nothing is owed until {interval} has elapsed");

        Advance(margin);
        await LoopWaits.UntilAsync(fired, $"cross {interval}", patience, cancellationToken);
    }

    /// <summary>The earliest park no straddle has taken yet, so a second straddle waits for its own
    /// park rather than re-reading the first one's.</summary>
    private async Task<TimeSpan> NextParkAsync(TimeSpan patience, CancellationToken cancellationToken)
    {
        TimeSpan? park = null;
        await LoopWaits.UntilAsync(
            () => (park = TryTakeNextPark()) is not null, "park", patience, cancellationToken);
        return park!.Value;
    }

    private TimeSpan? TryTakeNextPark()
    {
        lock (_gate)
        {
            return _claimed < _parks.Count ? _parks[_claimed++] : null;
        }
    }
}
