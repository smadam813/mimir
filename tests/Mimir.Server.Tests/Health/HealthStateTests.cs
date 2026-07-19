using Mimir.Contracts.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Tests.Health;

public class HealthStateTests
{
    [Fact]
    public void StartsPending_BeforeAnythingHasReported()
    {
        var state = new HealthState();

        state.Current.Ollama.State.ShouldBe(HealthTileState.Pending);
        state.Current.Ollama.Models.ShouldBeEmpty();
        state.Current.Storage.State.ShouldBe(HealthTileState.Pending);
        state.Current.Storage.DatabaseSizeBytes.ShouldBeNull();
    }

    [Fact]
    public void Update_ReplacesTheSnapshot()
    {
        var state = new HealthState();

        state.Update(s => s with { Storage = s.Storage with { State = HealthTileState.Ready, DatabaseSizeBytes = 8_192 } });

        state.Current.Storage.DatabaseSizeBytes.ShouldBe(8_192);
        state.Current.Ollama.State.ShouldBe(HealthTileState.Pending, "an unrelated tile is untouched");
    }

    [Fact]
    public void Update_NotifiesEverySubscriberWithTheNewSnapshot()
    {
        var state = new HealthState();
        var first = new List<HealthSnapshot>();
        var second = new List<HealthSnapshot>();
        using var _ = state.Subscribe(first.Add);
        using var __ = state.Subscribe(second.Add);

        state.Update(s => s with { Storage = s.Storage with { State = HealthTileState.Ready } });

        first.ShouldHaveSingleItem().Storage.State.ShouldBe(HealthTileState.Ready);
        second.ShouldHaveSingleItem().Storage.State.ShouldBe(HealthTileState.Ready);
    }

    [Fact]
    public void DisposedSubscription_StopsReceivingUpdates()
    {
        var state = new HealthState();
        var received = new List<HealthSnapshot>();
        var subscription = state.Subscribe(received.Add);

        state.Update(s => s);
        subscription.Dispose();
        state.Update(s => s);

        received.Count.ShouldBe(1);
    }

    [Fact]
    public void DisposingTwice_IsHarmless()
    {
        var state = new HealthState();
        var subscription = state.Subscribe(_ => { });

        subscription.Dispose();
        Should.NotThrow(subscription.Dispose);
    }

    [Fact]
    public void AThrowingSubscriber_DoesNotStarveTheOthersOrTheUpdate()
    {
        // A dead Blazor circuit must never take down health reporting for the rest of the app.
        var state = new HealthState();
        var survivor = new List<HealthSnapshot>();
        using var _ = state.Subscribe(_ => throw new InvalidOperationException("circuit is gone"));
        using var __ = state.Subscribe(survivor.Add);

        Should.NotThrow(() => state.Update(s => s with { Storage = s.Storage with { State = HealthTileState.Ready } }));

        survivor.ShouldHaveSingleItem();
        state.Current.Storage.State.ShouldBe(HealthTileState.Ready);
    }

    [Fact]
    public void ConcurrentUpdates_AreAppliedAtomically()
    {
        var state = new HealthState();

        Parallel.For(0, 500, _ => state.Update(s => s with
        {
            Storage = s.Storage with { DatabaseSizeBytes = (s.Storage.DatabaseSizeBytes ?? 0) + 1 },
        }));

        state.Current.Storage.DatabaseSizeBytes.ShouldBe(500);
    }
}
