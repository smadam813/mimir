using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Hooks;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

/// <summary>
/// Spec §4: creates or resumes the Episode from the session id and the CLI-resolved Project, and
/// appends Events in arrival order. Capture is dumb (ADR-0003): no judgment, no models — the only
/// transformations are the §4 payload truncation and the Seal on SessionEnd.
/// </summary>
internal sealed class CaptureService(
    MimirDbContext db,
    ProjectResolver projects,
    IOptions<CaptureOptions> options,
    TimeProvider clock,
    IEpisodeFeed feed)
{
    public async Task<Episode> ResumeEpisodeAsync(HookEventRequest request, CancellationToken cancellationToken)
        => await GetOrCreateEpisodeAsync(request, cancellationToken);

    public async Task<Event> AppendEventAsync(
        HookEventRequest request,
        EventType type,
        CancellationToken cancellationToken)
        => await AppendEventAsync(
            await GetOrCreateEpisodeAsync(request, cancellationToken), request, type, cancellationToken);

    /// <summary>
    /// Append to an Episode the caller already resolved — the §4 single round-trip resolves once
    /// and shares it between capture and recall rather than looking it up twice on the 500 ms path.
    /// </summary>
    public async Task<Event> AppendEventAsync(
        Episode episode,
        HookEventRequest request,
        EventType type,
        CancellationToken cancellationToken)
    {
        var truncated = PayloadTruncator.Truncate(request.Payload, options.Value);

        for (var attempt = 1; ; attempt++)
        {
            var lastSeq = await db.Events
                .Where(e => e.EpisodeId == episode.Id)
                .MaxAsync(e => (int?)e.Seq, cancellationToken) ?? 0;

            var evt = new Event
            {
                Id = Guid.CreateVersion7(),
                EpisodeId = episode.Id,
                Seq = lastSeq + 1,
                Type = type,
                At = clock.GetUtcNow(),
                Payload = truncated.Json,
                PayloadFullSize = truncated.FullSizeBytes,
                Salient = type == EventType.Remember,
            };
            db.Events.Add(evt);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                feed.Publish(new EpisodeChange(episode.ProjectId, episode.Id));
                return evt;
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation() && attempt < DbRaces.SeqRaceMaxAttempts)
            {
                // Lost the per-Episode seq race to a concurrent hook; take the next slot.
                db.Entry(evt).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Seals the Episode with the hook-reported reason (§4). Session end is not an Event; a
    /// duplicate SessionEnd changes nothing — the first Seal is the session's real end.
    /// </summary>
    public async Task SealEpisodeAsync(HookEventRequest request, CancellationToken cancellationToken)
    {
        var episode = await GetOrCreateEpisodeAsync(request, cancellationToken);
        if (episode.SealedAt is not null)
        {
            // A Seal is never unset, so a tracked sealed instance is always truthful; a stale
            // unsealed one falls through to the guarded update, which is safe either way.
            return;
        }

        var sealedAt = clock.GetUtcNow();
        var reason = request.Payload.StringProperty("reason");

        // The WHERE guard is first-seal-wins made atomic: a duplicate that lost the race updates
        // zero rows instead of overwriting the session's real end.
        // §6: Sealing sets distillation=pending. Creation already starts there (the §3 state set
        // has no earlier value), so this restate matters only to readers of the spec and this code.
        var sealedRows = await db.Episodes
            .Where(e => e.Id == episode.Id && e.SealedAt == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.SealedAt, sealedAt)
                    .SetProperty(e => e.SealReason, reason)
                    .SetProperty(e => e.Distillation, DistillationState.Pending),
                cancellationToken);
        if (sealedRows > 0)
        {
            feed.Publish(new EpisodeChange(episode.ProjectId, episode.Id));
        }
    }

    private async Task<Episode> GetOrCreateEpisodeAsync(
        HookEventRequest request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var episode = await db.Episodes.FirstOrDefaultAsync(
                e => e.SessionId == request.SessionId, cancellationToken);
            if (episode is not null)
            {
                return episode;
            }

            var project = await projects.ResolveAsync(
                request.ProjectIdentity, request.ProjectRoot, cancellationToken);
            episode = new Episode
            {
                Id = Guid.CreateVersion7(),
                SessionId = request.SessionId,
                ProjectId = project.Id,
                StartedAt = clock.GetUtcNow(),
                Cwd = request.Cwd,
                Distillation = DistillationState.Pending,
            };
            db.Episodes.Add(episode);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                feed.Publish(new EpisodeChange(episode.ProjectId, episode.Id));
                return episode;
            }
            catch (DbUpdateException ex) when (
                (ex.IsUniqueViolation() || ex.IsForeignKeyViolation()) && attempt < DbRaces.CreateRaceMaxAttempts)
            {
                // Unique violation: lost the unique-session race to a concurrent hook — resume the
                // winner's Episode. FK violation: a concurrent clone merge (#17) deleted the
                // resolved Project between resolve and insert — re-resolve; it finds the survivor.
                db.Entry(episode).State = EntityState.Detached;
            }
        }
    }
}
