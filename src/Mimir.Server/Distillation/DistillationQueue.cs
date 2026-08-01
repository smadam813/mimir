using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

internal sealed class DistillationQueue(
    MimirDbContext db,
    TimeProvider clock,
    IOptions<DistillationOptions> options)
{
    /// <summary>MaxValue, not <em>now</em>: a clock stepped back across a crash leaves a stamp in
    /// this process's future, and that claim still has to be taken back.</summary>
    private static readonly DateTimeOffset NothingIsFresh = DateTimeOffset.MaxValue;

    public async Task<Episode?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var episode = await db.Episodes
            .Where(e => e.SealedAt != null && e.Distillation == DistillationState.Pending)
            .OrderBy(e => e.SealedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (episode is null)
        {
            return null;
        }

        episode.Distillation = DistillationState.Running;
        episode.DistillationStartedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return episode;
    }

    /// <summary>Deliberately an untracked write rather than a tracked one.</summary>
    public async Task FailAsync(Guid episodeId, CancellationToken cancellationToken)
        => await db.Episodes
            .Where(e => e.Id == episodeId && e.Distillation == DistillationState.Running)
            .ExecuteUpdateAsync(
                update => update.SetProperty(e => e.Distillation, DistillationState.Failed),
                cancellationToken);

    public Func<MimirDbContext, CancellationToken, Task> CompleteMarker(Guid episodeId)
        => async (batch, cancellationToken) =>
        {
            // Read into a local: EF cannot translate a TimeProvider call inside SetProperty.
            var distilledAt = clock.GetUtcNow();
            await batch.Episodes
                .Where(e => e.Id == episodeId)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(e => e.Distillation, DistillationState.Done)
                        .SetProperty(e => e.DistilledAt, distilledAt),
                    cancellationToken);
        };

    public async Task<int> RequeueAbandonedAsync(CancellationToken cancellationToken)
        => await RequeueRunningAsync(NothingIsFresh, cancellationToken);

    public async Task<int> RequeueStaleAsync(CancellationToken cancellationToken)
        => await RequeueRunningAsync(clock.GetUtcNow() - options.Value.StaleRunningAfter, cancellationToken);

    public async Task<int> RequeueFailedAsync(CancellationToken cancellationToken)
        => await db.Episodes
            .Where(e => e.Distillation == DistillationState.Failed)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Distillation, DistillationState.Pending)
                    .SetProperty(e => e.DistillationStartedAt, (DateTimeOffset?)null),
                cancellationToken);

    public async Task<int> QueueDepthAsync(CancellationToken cancellationToken)
        => await db.Episodes.CountAsync(
            e => e.SealedAt != null && e.Distillation != DistillationState.Done,
            cancellationToken);

    private async Task<int> RequeueRunningAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => await db.Episodes
            .Where(e => e.Distillation == DistillationState.Running
                && (e.DistillationStartedAt == null || e.DistillationStartedAt < cutoff))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Distillation, DistillationState.Pending)
                    .SetProperty(e => e.DistillationStartedAt, (DateTimeOffset?)null),
                cancellationToken);
}
