using Mimir.Contracts.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Tests;

/// <summary>
/// Waiting on a health tile to reach a state. Every hosted service that owns a tile —
/// Distillation's worker, the Harvester, Storage's migrator — needs the same wait, and the race
/// handling is the whole of it: subscribe first, then read <see cref="HealthState.Current"/>,
/// because a tile that arrives between those two lines is not published to a subscriber who was
/// not there yet. Static, and over <see cref="HealthState"/> rather than on
/// <c>PostgresTestBase</c>, since <c>StorageServiceTests</c> deliberately has no Postgres.
/// </summary>
public static class HealthTileWaits
{
    public static async Task<TTile> TileAsync<TTile>(
        this HealthState health,
        Func<HealthSnapshot, TTile> select,
        Func<TTile, bool> accept,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        var seen = new TaskCompletionSource<TTile>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = health.Subscribe(snapshot =>
        {
            if (accept(select(snapshot)))
            {
                seen.TrySetResult(select(snapshot));
            }
        });

        var current = select(health.Current);
        return accept(current) ? current : await seen.Task.WaitAsync(patience, cancellationToken);
    }
}
