namespace Mimir.Server.Capture;

public readonly record struct EpisodeChange(Guid ProjectId, Guid EpisodeId);

public interface IEpisodeFeed
{
    void Publish(EpisodeChange change);

    IDisposable Subscribe(Action<EpisodeChange> onChange);
}

/// <inheritdoc cref="IEpisodeFeed"/>
public sealed class EpisodeFeed : IEpisodeFeed
{
    private readonly Lock _gate = new();
    private readonly HashSet<Subscription> _subscribers = [];

    public void Publish(EpisodeChange change)
    {
        Subscription[] subscribers;
        lock (_gate)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber.OnChange(change);
            }
            catch (Exception)
            {
                // Swallowed: a subscriber is a UI circuit that may already be tearing down.
            }
        }
    }

    public IDisposable Subscribe(Action<EpisodeChange> onChange)
    {
        var subscription = new Subscription(this, onChange);
        lock (_gate)
        {
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    private sealed class Subscription(EpisodeFeed owner, Action<EpisodeChange> onChange) : IDisposable
    {
        public Action<EpisodeChange> OnChange { get; } = onChange;

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._subscribers.Remove(this);
            }
        }
    }
}
