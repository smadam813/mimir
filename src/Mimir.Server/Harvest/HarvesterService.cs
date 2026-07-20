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
                var tick = Task.Delay(wait, clock, stoppingToken);
                var woken = await Task.WhenAny(tick, triggered);
                if (woken == triggered)
                {
                    triggered = trigger.WaitAsync(stoppingToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
