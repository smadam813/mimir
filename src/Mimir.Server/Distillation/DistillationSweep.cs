using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>What one sweep pass changed.</summary>
internal sealed record SweepResult(int CrashSealed, int StaleReset, int Requeued, int ContestedCleared)
{
    /// <summary>Whether the pass put anything on the queue — if so, the worker is worth waking.</summary>
    public bool QueueGrew => CrashSealed + StaleReset + Requeued > 0;
}

/// <summary>
/// The §6 sweep: crash-Seals unsealed Episodes idle past
/// <see cref="DistillationOptions.CrashSealIdleAfter"/> (§4, <c>seal_reason=crash-swept</c>), then
/// asks the <see cref="DistillationQueue"/> for its two recovery legs — stale <c>running</c> claims
/// reset and <c>failed</c> Episodes re-queued. A <c>done</c> Episode is never touched — re-processing
/// would push duplicate candidates through the Merge Gate and inflate Reinforcement. The §6.4
/// Contested clear rides along: this is the periodic pass the <see cref="ContestedSweep"/> always
/// said the Distiller could fold it into.
/// </summary>
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

        // Idle means no Event lately — a session is its Event stream, so the last Event's arrival
        // (or the Episode's start, before any arrive) is when it was last seen alive. The pending
        // restate below is one of the two deliberate queue writes outside DistillationQueue: the
        // sealed_at IS NULL guard makes it a no-op (an unsealed row is already pending), and it
        // rides along so this reads as the §6 enqueue it is.
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
