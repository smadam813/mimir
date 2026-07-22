using Mimir.Contracts.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Distillation;

/// <summary>
/// The §6 single worker and sole owner of the Distillation tile: wakes on a Seal's trigger (or
/// the idle poll, which is what makes the DB-state queue survive restarts and missed pokes),
/// drains the queue one Episode at a time, and reports depth + last run live. A failed Episode
/// degrades the tile and the loop backs off briefly before trying the next queue entry — the
/// failure is parked as <c>failed</c> for the sweep, never retried hot.
/// </summary>
internal sealed class DistillerService(
    IServiceScopeFactory scopeFactory,
    IDistillationTrigger trigger,
    IHealthState health,
    TimeProvider clock,
    ILogger<DistillerService> logger) : BackgroundService
{
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan IdlePollInterval = TimeSpan.FromMinutes(5);

    private bool _recovered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var triggered = trigger.WaitAsync(stoppingToken);
            while (true)
            {
                var wait = await WorkAsync(stoppingToken);
                if (wait is null)
                {
                    continue; // Something distilled — drain on without waiting.
                }

                using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var tick = Task.Delay(wait.Value, clock, tickCancellation.Token);
                var woken = await Task.WhenAny(tick, triggered);
                if (woken == triggered)
                {
                    triggered = trigger.WaitAsync(stoppingToken);
                    // The trigger won; release the pending timer now instead of leaving one
                    // abandoned Task.Delay alive per Seal until it lapses.
                    tickCancellation.Cancel();
                }

                await woken; // Rethrows the shutdown cancellation; a lapsed timer yields nothing.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Distiller stopped because the host is shutting down");
        }
    }

    /// <returns>How long to wait before the next look, or null to look again immediately.</returns>
    private async Task<TimeSpan?> WorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var run = scope.ServiceProvider.GetRequiredService<DistillationRun>();
            if (!_recovered)
            {
                var abandoned = await run.RequeueAbandonedAsync(cancellationToken);
                if (abandoned > 0)
                {
                    logger.LogInformation(
                        "Re-queued {Abandoned} Episode(s) a previous process left Running", abandoned);
                }

                _recovered = true;
            }

            var attempt = await run.RunNextAsync(cancellationToken);
            var depth = await run.QueueDepthAsync(cancellationToken);
            switch (attempt)
            {
                case null:
                    UpdateTile(HealthTileState.Ready, Describe(depth), depth);
                    return IdlePollInterval;
                case { Succeeded: true }:
                    UpdateTile(HealthTileState.Ready, Describe(depth), depth, lastRunAt: clock.GetUtcNow());
                    return null;
                default:
                    UpdateTile(HealthTileState.Degraded, attempt.Error!, depth, lastRunAt: clock.GetUtcNow());
                    return FailureRetryInterval;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only a shutdown cancellation may escape and stop the loop (the Harvester's rule).
            // Anything else — Postgres still migrating, say — degrades the tile and retries soon.
            logger.LogWarning(ex, "Distillation pass failed; retrying in {RetryInterval}", FailureRetryInterval);
            UpdateTile(HealthTileState.Degraded, ex.Message, depth: null);
            return FailureRetryInterval;
        }
    }

    private static string Describe(int depth)
        => depth == 0 ? "queue empty" : $"{depth} queued";

    /// <summary>Null figures keep the tile's last known ones — same contract as the Harvester.</summary>
    private void UpdateTile(HealthTileState state, string summary, int? depth, DateTimeOffset? lastRunAt = null)
        => health.Update(snapshot => snapshot with
        {
            Distillation = new DistillationTile
            {
                State = state,
                Summary = summary,
                QueueDepth = depth ?? snapshot.Distillation.QueueDepth,
                LastRunAt = lastRunAt ?? snapshot.Distillation.LastRunAt,
            },
        });
}
