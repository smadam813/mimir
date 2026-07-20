using System.Text.Json;
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
    TimeProvider clock)
{
    public async Task<Episode> ResumeEpisodeAsync(HookEventRequest request, CancellationToken cancellationToken)
        => await GetOrCreateEpisodeAsync(request, cancellationToken);

    public async Task<Event> AppendEventAsync(
        HookEventRequest request,
        EventType type,
        CancellationToken cancellationToken)
    {
        var episode = await GetOrCreateEpisodeAsync(request, cancellationToken);
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
                return evt;
            }
            catch (DbUpdateException) when (attempt < 5)
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
            return;
        }

        episode.SealedAt = clock.GetUtcNow();
        episode.SealReason =
            request.Payload.ValueKind == JsonValueKind.Object
            && request.Payload.TryGetProperty("reason", out var reason)
            && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
        await db.SaveChangesAsync(cancellationToken);
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
                return episode;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Lost the unique-session race to a concurrent hook; resume the winner's Episode.
                db.Entry(episode).State = EntityState.Detached;
            }
        }
    }
}
