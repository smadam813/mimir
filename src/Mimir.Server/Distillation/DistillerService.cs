using Mimir.Contracts.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Distillation;

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
                    continue;
                }

                using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var tick = Task.Delay(wait.Value, clock, tickCancellation.Token);
                var woken = await Task.WhenAny(tick, triggered);
                if (woken == triggered)
                {
                    triggered = trigger.WaitAsync(stoppingToken);
                    // Not redundant with the using: this releases the timer now rather than one
                    // Task.Delay later, so a Seal burst leaves none of them alive.
                    tickCancellation.Cancel();
                }

                await woken;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Distiller stopped because the host is shutting down");
        }
    }

    private async Task<TimeSpan?> WorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var queue = scope.ServiceProvider.GetRequiredService<DistillationQueue>();
            var run = scope.ServiceProvider.GetRequiredService<DistillationRun>();
            if (!_recovered)
            {
                var abandoned = await queue.RequeueAbandonedAsync(cancellationToken);
                if (abandoned > 0)
                {
                    logger.LogInformation(
                        "Re-queued {Abandoned} Episode(s) a previous process left Running", abandoned);
                }

                _recovered = true;
            }

            var attempt = await run.RunNextAsync(cancellationToken);
            var depth = await queue.QueueDepthAsync(cancellationToken);
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
            logger.LogWarning(ex, "Distillation pass failed; retrying in {RetryInterval}", FailureRetryInterval);
            UpdateTile(HealthTileState.Degraded, ex.Message, depth: null);
            return FailureRetryInterval;
        }
    }

    private static string Describe(int depth)
        => depth == 0 ? "queue empty" : $"{depth} queued";

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
