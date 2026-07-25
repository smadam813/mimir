using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>One worked queue entry: which Episode, whether it distilled, and what it yielded.</summary>
internal sealed record DistillationAttempt(Guid EpisodeId, bool Succeeded, int Candidates, string? Error);

/// <summary>
/// One turn of the §6 queue: claim an Episode from the <see cref="DistillationQueue"/>, distill its
/// Event stream, and hand the whole Episode to the Merge Gate as one Admission batch with the
/// queue's <c>done</c> marker as its finalizer — so a failure or crash anywhere leaves nothing
/// admitted and the sweep's re-queue redoes the Episode without inflating Reinforcement. The
/// distiller's model calls all happen before the batch; the gate owns the embeddings, the
/// transaction, and the commit. This is orchestration only: what a claim, a failure and a
/// completion do to the queue is the queue's to say.
/// </summary>
internal sealed class DistillationRun(
    MimirDbContext db,
    DistillationQueue queue,
    IEpisodeDistiller distiller,
    MergeGate gate,
    ILogger<DistillationRun> logger)
{
    /// <returns>The attempt, or null when the queue is empty.</returns>
    public async Task<DistillationAttempt?> RunNextAsync(CancellationToken cancellationToken)
    {
        var episode = await queue.ClaimNextAsync(cancellationToken);
        if (episode is null)
        {
            return null;
        }

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
            // The failed batch needs no cleanup here — it ran on a context of the gate's own
            // making (§6), which is why this handler no longer clears the change tracker.
            await queue.FailAsync(episode.Id, cancellationToken);
            return new DistillationAttempt(episode.Id, Succeeded: false, Candidates: 0, ex.Message);
        }
    }

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
    /// The Episode's candidates as one Admission batch, finalized by the queue's <c>done</c> marker
    /// on the gate's batch context so the marker commits with the Wisdom the Episode produced or
    /// not at all.
    /// </summary>
    private async Task AdmitAsync(
        Episode episode, IReadOnlyList<WisdomCandidate> candidates, CancellationToken cancellationToken)
    {
        await gate.AdmitAllAsync(candidates, queue.CompleteMarker(episode.Id), cancellationToken);

        // The marker committed on the gate's context, so the copy this run still tracks would
        // read Running — the claim it was given, not the state it is in. Nothing writes that copy
        // back today, but anything that reads it after this point should read the truth.
        await db.Entry(episode).ReloadAsync(cancellationToken);
    }
}
