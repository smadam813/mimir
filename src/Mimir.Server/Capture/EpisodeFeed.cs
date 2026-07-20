namespace Mimir.Server.Capture;

/// <summary>Something about an Episode changed: created, grew an Event, sealed, or was deleted.</summary>
public readonly record struct EpisodeChange(Guid ProjectId, Guid EpisodeId);

/// <summary>
/// How spec §8.2's timeline is live: capture (and the UI's own hard deletes) publish here after
/// each committed write, Blazor circuits subscribe and re-query. Carries only identities — the
/// database stays the single source of truth for what actually changed.
/// </summary>
public interface IEpisodeFeed
{
    void Publish(EpisodeChange change);

    /// <summary>Observes every subsequent change until the returned handle is disposed.</summary>
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
            // A subscriber is a UI circuit that may already be tearing down. One dead circuit
            // must not stop the others from updating, nor abort the capture that published.
            try
            {
                subscriber.OnChange(change);
            }
            catch (Exception)
            {
                // Intentionally swallowed; see above.
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
