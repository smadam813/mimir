using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;

namespace Mimir.Server.Distillation;

/// <summary>
/// §6.4's second half: <c>contested_at</c> is cleared once it has stood for
/// <see cref="DistillationOptions.ContestedDuration"/> — the flag marks *recently* adjudicated
/// Wisdom for the UI, not a permanent stain.
/// </summary>
internal sealed class ContestedSweep(MimirDbContext db, IOptions<DistillationOptions> options, TimeProvider clock)
{
    /// <returns>How many Wisdom rows had their expired Contested flag cleared.</returns>
    public async Task<int> ClearExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - options.Value.ContestedDuration;
        return await db.Wisdom
            .Where(w => w.ContestedAt != null && w.ContestedAt <= cutoff)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(w => w.ContestedAt, (DateTimeOffset?)null),
                cancellationToken);
    }
}

/// <summary>
/// Runs the <see cref="ContestedSweep"/> on boot and then hourly — resolution enough for a flag
/// that lives 14 days. A failed pass (Postgres still migrating) just waits for the next tick;
/// the Distiller ticket (#22) may fold this into its 6 h sweep.
/// </summary>
internal sealed class ContestedSweepService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<ContestedSweepService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Contested sweep stopped because the host is shutting down");
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var cleared = await scope.ServiceProvider.GetRequiredService<ContestedSweep>()
                .ClearExpiredAsync(cancellationToken);
            if (cleared > 0)
            {
                logger.LogInformation("Cleared the expired Contested flag on {Cleared} Wisdom row(s)", cleared);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Contested sweep failed; next attempt in {Interval}", Interval);
        }
    }
}
