using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

internal sealed record DistillationAttempt(Guid EpisodeId, bool Succeeded, int Candidates, string? Error);

internal sealed class DistillationRun(
    MimirDbContext db,
    DistillationQueue queue,
    IEpisodeDistiller distiller,
    MergeGate gate,
    ILogger<DistillationRun> logger)
{
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
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Distilling Episode {EpisodeId} failed; the sweep will re-queue it", episode.Id);
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

    private async Task AdmitAsync(
        Episode episode, IReadOnlyList<WisdomCandidate> candidates, CancellationToken cancellationToken)
    {
        await gate.AdmitAllAsync(candidates, queue.CompleteMarker(episode.Id), cancellationToken);

        await db.Entry(episode).ReloadAsync(cancellationToken);
    }
}
