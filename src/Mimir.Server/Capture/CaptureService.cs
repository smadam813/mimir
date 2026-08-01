using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Hooks;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

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

    public async Task<Event> AppendEventAsync(
        Episode episode,
        HookEventRequest request,
        EventType type,
        CancellationToken cancellationToken)
    {
        var truncated = PayloadTruncator.Truncate(request.Payload, options.Value);
        return await AppendAsync(episode, truncated.Json, truncated.FullSizeBytes, type, cancellationToken);
    }

    public async Task<Event> AppendVerbatimEventAsync(
        Episode episode,
        JsonElement payload,
        EventType type,
        CancellationToken cancellationToken)
    {
        var json = payload.GetRawText();
        return await AppendAsync(
            episode, json, Encoding.UTF8.GetByteCount(json), type, cancellationToken);
    }

    private async Task<Event> AppendAsync(
        Episode episode,
        string payloadJson,
        int fullSizeBytes,
        EventType type,
        CancellationToken cancellationToken)
    {
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
                Payload = payloadJson,
                PayloadFullSize = fullSizeBytes,
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
                db.Entry(evt).State = EntityState.Detached;
            }
        }
    }

    public async Task SealEpisodeAsync(HookEventRequest request, CancellationToken cancellationToken)
    {
        var episode = await GetOrCreateEpisodeAsync(request, cancellationToken);
        if (episode.SealedAt is not null)
        {
            return;
        }

        var sealedAt = clock.GetUtcNow();
        var reason = request.Payload.StringProperty("reason");

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
                db.Entry(episode).State = EntityState.Detached;
            }
        }
    }
}
