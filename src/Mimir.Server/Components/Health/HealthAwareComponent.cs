using Microsoft.AspNetCore.Components;
using Mimir.Contracts.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Components.Health;

public abstract class HealthAwareComponent : ComponentBase, IDisposable
{
    private IDisposable? _subscription;

    [Inject]
    private IHealthState HealthState { get; set; } = default!;

    protected HealthSnapshot Snapshot { get; private set; } = HealthSnapshot.Pending;

    protected override void OnInitialized()
    {
        Snapshot = HealthState.Current;

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
