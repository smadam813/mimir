using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>One worked queue entry: which Episode, whether it distilled, and what it yielded.</summary>
internal sealed record DistillationAttempt(Guid EpisodeId, bool Succeeded, int Candidates, string? Error);

/// <summary>
/// One turn of the §6 queue: claim the oldest-Sealed pending Episode (<c>pending → running</c>),
/// distill its Event stream, and hand the whole Episode to the Merge Gate as one Admission batch
/// with the <c>done</c> marker as its finalizer — so a failure or crash anywhere leaves nothing
/// admitted and the sweep's re-queue redoes the Episode without inflating Reinforcement. The
/// distiller's model calls all happen before the batch; the gate owns the embeddings, the
/// transaction, and the commit. Failure marks <c>failed</c> for the sweep to re-queue.
/// </summary>
internal sealed class DistillationRun(
    MimirDbContext db,
    EpisodeDistiller distiller,
    MergeGate gate,
    TimeProvider clock,
    ILogger<DistillationRun> logger)
{
    /// <returns>The attempt, or null when the queue is empty.</returns>
    public async Task<DistillationAttempt?> RunNextAsync(CancellationToken cancellationToken)
    {
        // Only Sealed Episodes queue: an unsealed row is a live session sitting at the §3 state
        // set's starting value, not work. Oldest Seal first. No claim race exists to guard
        // against — §6's single-worker rule is what lets concurrent gate admissions be ignored.
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

        try
        {
            var candidates = await DistillAsync(episode, cancellationToken);
            await AdmitAsync(episode, candidates, cancellationToken);
            logger.LogInformation(
                "Distilled Episode {EpisodeId} into {Candidates} candidate(s)", episode.Id, candidates.Count);
            return new DistillationAttempt(episode.Id, Succeeded: true, candidates.Count, Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-run: nothing admitted (the gate's transaction never committed). The
            // claim stays Running; the worker's boot recovery re-queues it on the next start.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Distilling Episode {EpisodeId} failed; the sweep will re-queue it", episode.Id);
            // The gate clears what its own rolled-back batch staged; this covers the rest of the
            // turn — the finalizer's detached done marker, anything the distill step tracked — so
            // nothing from a failed run rides a later save on this scoped context.
            db.ChangeTracker.Clear();
            await db.Episodes
                .Where(e => e.Id == episode.Id && e.Distillation == DistillationState.Running)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(e => e.Distillation, DistillationState.Failed),
                    cancellationToken);
            return new DistillationAttempt(episode.Id, Succeeded: false, Candidates: 0, ex.Message);
        }
    }

    /// <summary>The §8 tile figure: Sealed Episodes still owed distillation.</summary>
    public async Task<int> QueueDepthAsync(CancellationToken cancellationToken)
        => await db.Episodes.CountAsync(
            e => e.SealedAt != null
                && (e.Distillation == DistillationState.Pending || e.Distillation == DistillationState.Running),
            cancellationToken);

    /// <summary>
    /// A Running claim found at boot is a previous process's — §6's single worker means no one
    /// else can hold one — so re-queue it now instead of leaving it to the sweep's stale window.
    /// </summary>
    public async Task<int> RequeueAbandonedAsync(CancellationToken cancellationToken)
        => await db.Episodes
            .Where(e => e.Distillation == DistillationState.Running)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Distillation, DistillationState.Pending)
                    .SetProperty(e => e.DistillationStartedAt, (DateTimeOffset?)null),
                cancellationToken);

    private async Task<IReadOnlyList<WisdomCandidate>> DistillAsync(
        Episode episode, CancellationToken cancellationToken)
    {
        var identity = await db.Projects
            .Where(p => p.Id == episode.ProjectId)
            .Select(p => p.Identity)
            .SingleAsync(cancellationToken);
        var events = await db.Events
            .Where(e => e.EpisodeId == episode.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken);
        return await distiller.DistillAsync(episode, identity, events, cancellationToken);
    }

    /// <summary>
    /// The Episode's candidates as one Admission batch, its <c>done</c> marker staged by the
    /// finalizer so the marker commits with the Wisdom the Episode produced or not at all.
    /// </summary>
    private async Task AdmitAsync(
        Episode episode, IReadOnlyList<WisdomCandidate> candidates, CancellationToken cancellationToken)
        => await gate.AdmitAllAsync(
            candidates,
            _ =>
            {
                episode.Distillation = DistillationState.Done;
                episode.DistilledAt = clock.GetUtcNow();
                return Task.CompletedTask;
            },
            cancellationToken);
}
