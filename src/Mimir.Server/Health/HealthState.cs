using Mimir.Contracts.Health;

namespace Mimir.Server.Health;

public interface IHealthState
{
    HealthSnapshot Current { get; }

    void Update(Func<HealthSnapshot, HealthSnapshot> mutate);

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
            try
            {
                subscriber.OnChanged(updated);
            }
            catch (Exception)
            {
                // Swallowed deliberately: one dead circuit must starve neither the others nor
                // the probe that pushed.
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
