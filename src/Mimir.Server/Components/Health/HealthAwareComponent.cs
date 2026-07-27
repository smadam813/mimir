using Microsoft.AspNetCore.Components;
using Mimir.Contracts.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Components.Health;

/// <summary>
/// The subscribe-render-dispose half of a component that reads the live health snapshot. Two do —
/// the Health pill and, on first run, the model-pull chip (#90) — and the shape was the same in
/// both down to the comment: take the current snapshot, then re-render on every push. One
/// statement of it, so a fix to how a torn-down circuit is handled lands on both consumers.
/// </summary>
public abstract class HealthAwareComponent : ComponentBase, IDisposable
{
    private IDisposable? _subscription;

    [Inject]
    private IHealthState HealthState { get; set; } = default!;

    /// <summary>The latest snapshot the probes have published.</summary>
    protected HealthSnapshot Snapshot { get; private set; } = HealthSnapshot.Pending;

    protected override void OnInitialized()
    {
        Snapshot = HealthState.Current;

        // Probes push from background threads; hop onto the circuit's dispatcher to re-render.
        _subscription = HealthState.Subscribe(snapshot => InvokeAsync(() =>
        {
            Snapshot = snapshot;
            StateHasChanged();
        }));
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        GC.SuppressFinalize(this);
    }
}
