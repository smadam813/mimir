using Mimir.Contracts.Health;

namespace Mimir.Server.Health;

/// <summary>
/// The health strip's single source of truth. Background probes push into it; Blazor circuits
/// subscribe and re-render, which is how spec §8's "live updates over the SignalR circuit" works
/// without any polling.
/// </summary>
public interface IHealthState
{
    HealthSnapshot Current { get; }

    /// <summary>Applies <paramref name="mutate"/> to the current snapshot and notifies subscribers.</summary>
    void Update(Func<HealthSnapshot, HealthSnapshot> mutate);

    /// <summary>Observes every subsequent snapshot until the returned handle is disposed.</summary>
    IDisposable Subscribe(Action<HealthSnapshot> onChanged);
}

/// <inheritdoc cref="IHealthState"/>
public sealed class HealthState : IHealthState
{
    private readonly Lock _gate = new();
    private readonly HashSet<Subscription> _subscribers = [];
    private HealthSnapshot _current = HealthSnapshot.Pending;

    public HealthSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(Func<HealthSnapshot, HealthSnapshot> mutate)
    {
        HealthSnapshot updated;
        Subscription[] subscribers;

        lock (_gate)
        {
            updated = _current = mutate(_current);
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            // A subscriber is a UI circuit that may already be tearing down. One dead circuit
            // must not stop the others from updating, nor abort the probe that pushed this.
            try
            {
                subscriber.OnChanged(updated);
            }
            catch (Exception)
            {
                // Intentionally swallowed; see above.
            }
        }
    }

    public IDisposable Subscribe(Action<HealthSnapshot> onChanged)
    {
        var subscription = new Subscription(this, onChanged);
        lock (_gate)
        {
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    private sealed class Subscription(HealthState owner, Action<HealthSnapshot> onChanged) : IDisposable
    {
        public Action<HealthSnapshot> OnChanged { get; } = onChanged;

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._subscribers.Remove(this);
            }
        }
    }
}
