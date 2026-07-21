using Microsoft.Extensions.Options;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Health;

namespace Mimir.Server.Harvest;

/// <summary>
/// The §5 hosted service and sole owner of the Harvester tile: scans on boot (the first scan of a
/// fresh database is the Backfill), then every <see cref="HarvestOptions.ScanInterval"/>, and
/// opportunistically whenever the trigger fires. A failed scan — Postgres still migrating, the
/// mount missing — degrades the tile and retries soon rather than sitting on the full interval.
/// </summary>
internal sealed class HarvesterService(
    IServiceScopeFactory scopeFactory,
    IHarvestScanTrigger trigger,
    IHealthState health,
    IOptions<HarvestOptions> options,
    TimeProvider clock,
    ILogger<HarvesterService> logger) : BackgroundService
{
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var triggered = trigger.WaitAsync(stoppingToken);
            while (true)
            {
                var scanned = await ScanAsync(stoppingToken);

                var wait = scanned ? options.Value.ScanInterval : FailureRetryInterval;
                using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var tick = Task.Delay(wait, clock, tickCancellation.Token);
                var woken = await Task.WhenAny(tick, triggered);
                if (woken == triggered)
                {
                    triggered = trigger.WaitAsync(stoppingToken);
                    // The trigger won; release the pending timer now instead of leaving one
                    // abandoned Task.Delay alive per SessionEnd until it lapses.
                    tickCancellation.Cancel();
                }

                await woken; // Rethrows the shutdown cancellation; a lapsed timer yields nothing.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Harvester stopped because the host is shutting down");
        }
    }

    private async Task<bool> ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scanner = scope.ServiceProvider.GetRequiredService<HarvestScanner>();
            var result = await scanner.ScanAsync(cancellationToken);

            // The §5 handoff rides the scan cadence: whatever versions this or any earlier scan
            // stored (the Backfill included) go through the Merge Gate now. A failure here — the
            // embedding model still provisioning, say — degrades the tile and retries soon; the
            // conversion marker means the retry picks up exactly where this run stopped.
            var converter = scope.ServiceProvider.GetRequiredService<HarvestConverter>();
            await converter.ConvertPendingAsync(cancellationToken);

            health.Update(snapshot => snapshot with
            {
                Harvester = new HarvesterTile
                {
                    State = HealthTileState.Ready,
                    Summary = $"{result.Items} {(result.Items == 1 ? "item" : "items")} · {result.Changed} changed",
                    LastScanAt = clock.GetUtcNow(),
                    Items = result.Items,
                    Changed = result.Changed,
                },
            });
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only a shutdown cancellation is allowed to escape and stop the loop. Any other
            // OperationCanceledException (e.g. a query timeout surfaced as one) is a failed scan
            // to degrade-and-retry, not a reason to tear the whole host down. This filter is the
            // inverse of ExecuteAsync's "genuine shutdown" catch — keep the two in sync.
            logger.LogWarning(ex, "Harvest scan failed; retrying in {RetryInterval}", FailureRetryInterval);
            health.Update(snapshot => snapshot with
            {
                Harvester = snapshot.Harvester with
                {
                    State = HealthTileState.Degraded,
                    Summary = ex.Message,
                },
            });
            return false;
        }
    }
}
