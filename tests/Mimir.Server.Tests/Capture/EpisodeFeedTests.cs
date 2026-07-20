using Mimir.Server.Capture;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// The feed is what makes spec §8.2's timeline live: capture publishes, UI circuits subscribe.
/// Same contract as the health strip's state — one dead subscriber never silences the rest.
/// </summary>
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
        // A subscriber is a UI circuit that may be tearing down; its failure must not stop other
        // circuits from updating, nor abort the capture request that published.
        var feed = new EpisodeFeed();
        var received = new List<EpisodeChange>();
        feed.Subscribe(_ => throw new InvalidOperationException("circuit gone"));
        feed.Subscribe(received.Add);

        Should.NotThrow(() => feed.Publish(Change));

        received.ShouldBe([Change]);
    }
}
