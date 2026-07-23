using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mimir.Contracts.Mcp;
using Mimir.Server.Capture;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// <c>mimir_remember</c> (§4, §7.1): the explicit save. Binds to the most recently active
/// unsealed Episode of the requester's Project as a <c>Remember</c> Event (<c>salient=true</c>);
/// with no unsealed Episode the content goes straight to the Merge Gate as a candidate — a
/// deliberate save is never dropped. The Project is resolved-or-created (§3.1): a save from a
/// directory Mimir has never seen still lands.
/// </summary>
internal sealed class McpRememberService(
    MimirDbContext db,
    ProjectResolver projects,
    CaptureService capture,
    MergeGate gate)
{
    public async Task<string> RememberAsync(McpRememberRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<WisdomKind>(request.Kind, ignoreCase: true, out var kind))
        {
            return McpTexts.UnknownKind(request.Kind);
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return "Nothing to remember — content is empty.";
        }

        var project = await projects.ResolveAsync(
            request.ProjectIdentity, request.ProjectRoot, cancellationToken);

        // "Most recently active" = last Event, else start (§7.1). The unsealed set per Project is
        // tiny, so the pick happens in memory rather than bending EF around a COALESCE-of-Max.
        var unsealed = await db.Episodes
            .Where(e => e.ProjectId == project.Id && e.SealedAt == null)
            .Select(e => new
            {
                Episode = e,
                LastEventAt = db.Events
                    .Where(ev => ev.EpisodeId == e.Id)
                    .Max(ev => (DateTimeOffset?)ev.At),
            })
            .ToListAsync(cancellationToken);
        var target = unsealed
            .OrderByDescending(e => e.LastEventAt ?? e.Episode.StartedAt)
            .ThenByDescending(e => e.Episode.StartedAt)
            .FirstOrDefault()?.Episode;

        if (target is not null)
        {
            // Verbatim: this payload is server-composed, not untrusted hook output — §4
            // truncation does not apply, and a deliberate save is never clipped (§7.1).
            var payload = JsonSerializer.SerializeToElement(
                new { content = request.Content, kind = kind.ToString() });
            await capture.AppendVerbatimEventAsync(target, payload, EventType.Remember, cancellationToken);
            return $"Remembered ({kind}, salient) in the live episode of {project.DisplayName}"
                + $" (session {target.SessionId}).";
        }

        await gate.AdmitAsync(new WisdomCandidate(kind, project.Id, request.Content), cancellationToken);
        return $"No live episode for {project.DisplayName} — the content went straight to the"
            + $" Merge Gate as a {kind} candidate.";
    }
}
