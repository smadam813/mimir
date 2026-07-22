using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;

namespace Mimir.Server.Distillation;

/// <summary>
/// Runs the <see cref="DistillationSweep"/> on boot and then every
/// <see cref="DistillationOptions.SweepInterval"/> (§6: 6 h), poking the worker whenever a pass
/// put anything back on the queue. A failed pass just waits for the next tick.
/// </summary>
internal sealed class DistillationSweepService(
    IServiceScopeFactory scopeFactory,
    IDistillationTrigger trigger,
    IOptions<DistillationOptions> options,
    TimeProvider clock,
    ILogger<DistillationSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(options.Value.SweepInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Distillation sweep stopped because the host is shutting down");
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<DistillationSweep>()
                .SweepAsync(cancellationToken);
            if (result != new SweepResult(0, 0, 0, 0))
            {
                logger.LogInformation(
                    "Sweep crash-Sealed {CrashSealed}, reset {StaleReset} stale claim(s), re-queued "
                    + "{Requeued} failed Episode(s), cleared {ContestedCleared} Contested flag(s)",
                    result.CrashSealed, result.StaleReset, result.Requeued, result.ContestedCleared);
            }

            if (result.QueueGrew)
            {
                trigger.Request();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Distillation sweep failed; next attempt in {Interval}", options.Value.SweepInterval);
        }
    }
}
