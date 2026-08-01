using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

internal sealed record SweepResult(int CrashSealed, int StaleReset, int Requeued, int ContestedCleared)
{
    public bool QueueGrew => CrashSealed + StaleReset + Requeued > 0;
}

internal sealed class DistillationSweep(
    MimirDbContext db,
    DistillationQueue queue,
    ContestedSweep contested,
    IOptions<DistillationOptions> options,
    TimeProvider clock)
{
    public async Task<SweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var idleCutoff = now - options.Value.CrashSealIdleAfter;
        var crashSealed = await db.Episodes
            .Where(e => e.SealedAt == null
                && (db.Events
                        .Where(ev => ev.EpisodeId == e.Id)
                        .Max(ev => (DateTimeOffset?)ev.At) ?? e.StartedAt) < idleCutoff)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.SealedAt, now)
                    .SetProperty(e => e.SealReason, Episode.CrashSweptReason)
                    .SetProperty(e => e.Distillation, DistillationState.Pending),
                cancellationToken);

        var staleReset = await queue.RequeueStaleAsync(cancellationToken);
        var requeued = await queue.RequeueFailedAsync(cancellationToken);

        var contestedCleared = await contested.ClearExpiredAsync(cancellationToken);
        return new SweepResult(crashSealed, staleReset, requeued, contestedCleared);
    }
}
