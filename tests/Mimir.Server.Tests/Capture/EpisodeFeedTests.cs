using Mimir.Server.Capture;

namespace Mimir.Server.Tests.Capture;

public sealed class EpisodeFeedTests
{
    private static readonly EpisodeChange Change = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void APublishedChange_ReachesEverySubscriber()
    {
        var feed = new EpisodeFeed();
        var first = new List<EpisodeChange>();
        var second = new List<EpisodeChange>();
        feed.Subscribe(first.Add);
        feed.Subscribe(second.Add);

        feed.Publish(Change);

        first.ShouldBe([Change]);
        second.ShouldBe([Change]);
    }

    [Fact]
    public void ADisposedSubscription_StopsReceiving()
    {
        var feed = new EpisodeFeed();
        var received = new List<EpisodeChange>();
        var subscription = feed.Subscribe(received.Add);

        subscription.Dispose();
        feed.Publish(Change);

        received.ShouldBeEmpty();
    }

    [Fact]
    public void AThrowingSubscriber_NeverSilencesTheOthers()
    {
        var feed = new EpisodeFeed();
        var received = new List<EpisodeChange>();
        feed.Subscribe(_ => throw new InvalidOperationException("circuit gone"));
        feed.Subscribe(received.Add);

        Should.NotThrow(() => feed.Publish(Change));

        received.ShouldBe([Change]);
    }

    [Fact]
    public void AnEpisodeChange_CarriesIdentitiesAndNothingElse()
    {
        // §8.2's live list re-queries on every notification, and this is the whole reason it can:
        // the message names what changed and the database stays the single source of truth for
        // what it changed to. Carry entity state here — a seal flag, an Event count — and a
        // circuit can render it instead of re-querying, which races the writes still in flight.
        typeof(EpisodeChange).GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .ShouldBe([("ProjectId", typeof(Guid)), ("EpisodeId", typeof(Guid))], ignoreOrder: true);
    }
}
