using Microsoft.EntityFrameworkCore;
using Mimir.Server.Health;

namespace Mimir.Server.Storage;

/// <summary>
/// Sole owner of the Storage tile: migrates the database (retrying until Postgres answers, which
/// is how "mimir waits on postgres" holds even without Compose's healthchecks), then keeps the
/// tile current. Running this in the background rather than at startup means the health strip is
/// visible — and reporting what it is waiting for — while Postgres is still booting.
/// </summary>
internal sealed class StorageService(
    IServiceScopeFactory scopeFactory,
    IHealthState health,
    TimeProvider timeProvider,
    ILogger<StorageService> logger) : BackgroundService
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await MigrateAsync(stoppingToken);

            using var timer = new PeriodicTimer(ProbeInterval, timeProvider);
            do
            {
                await ProbeAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Storage service stopped because the host is shutting down");
        }
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<MimirDbContext>();
                await context.Database.MigrateAsync(cancellationToken);

                logger.LogInformation("Database schema is up to date");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not migrate the database; retrying in {RetryInterval}", RetryInterval);
                health.Update(snapshot => snapshot with { Storage = StorageTileFactory.Unreachable(ex.Message) });

                await Task.Delay(RetryInterval, timeProvider, cancellationToken);
            }
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<IStorageProbe>();

        var tile = await probe.ProbeAsync(cancellationToken);
        health.Update(snapshot => snapshot with { Storage = tile });
    }
}
