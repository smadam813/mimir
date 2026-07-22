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
/// The §6 sweep: re-queues <c>failed</c>, resets <c>running</c> claims stale past
/// <see cref="DistillationOptions.StaleRunningAfter"/>, and crash-Seals unsealed Episodes idle
/// past <see cref="DistillationOptions.CrashSealIdleAfter"/> (§4, <c>seal_reason=crash-swept</c>).
/// A <c>done</c> Episode is never touched — re-processing would push duplicate candidates through
/// the Merge Gate and inflate Reinforcement. The §6.4 Contested clear rides along: this is the
/// periodic pass the <see cref="ContestedSweep"/> always said the Distiller could fold it into.
/// </summary>
internal sealed class DistillationSweep(
    MimirDbContext db,
    ContestedSweep contested,
    IOptions<DistillationOptions> options,
    TimeProvider clock)
{
    public async Task<SweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Idle means no Event lately — a session is its Event stream, so the last Event's arrival
        // (or the Episode's start, before any arrive) is when it was last seen alive.
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

        // A Running claim without a start stamp cannot prove it is fresh; only the live worker's
        // own claim (always stamped) is ever entitled to Running, so both go back to the queue.
        var staleCutoff = now - options.Value.StaleRunningAfter;
        var staleReset = await db.Episodes
            .Where(e => e.Distillation == DistillationState.Running
                && (e.DistillationStartedAt == null || e.DistillationStartedAt < staleCutoff))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Distillation, DistillationState.Pending)
                    .SetProperty(e => e.DistillationStartedAt, (DateTimeOffset?)null),
                cancellationToken);

        var requeued = await db.Episodes
            .Where(e => e.Distillation == DistillationState.Failed)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Distillation, DistillationState.Pending)
                    .SetProperty(e => e.DistillationStartedAt, (DateTimeOffset?)null),
                cancellationToken);

        var contestedCleared = await contested.ClearExpiredAsync(cancellationToken);
        return new SweepResult(crashSealed, staleReset, requeued, contestedCleared);
    }
}
