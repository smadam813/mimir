using Microsoft.Extensions.Options;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Health;

namespace Mimir.Server.Harvest;

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
                    // Looks redundant next to the using, and is not: nothing observes the timer
                    // this abandons, so no test can catch its removal.
                    tickCancellation.Cancel();
                }

                await woken;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Harvester stopped because the host is shutting down");
        }
    }

    private async Task<bool> ScanAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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

        try
        {
            var converter = scope.ServiceProvider.GetRequiredService<HarvestConverter>();
            await converter.ConvertPendingAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Merge Gate conversion failed; retrying in {RetryInterval}", FailureRetryInterval);
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
