using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mimir.Contracts.Mcp;
using Mimir.Server.Capture;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

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
            var payload = JsonSerializer.SerializeToElement(
                new { content = request.Content, kind = kind.ToString() });
            await capture.AppendVerbatimEventAsync(target, payload, EventType.Remember, cancellationToken);
            return $"Remembered ({kind}, salient) in the live episode of {project.DisplayName}"
                + $" (session {target.SessionId}).";
        }

        await gate.AdmitAllAsync(
            [new WisdomCandidate(kind, project.Id, request.Content)], finalizer: null, CancellationToken.None);
        return $"No live episode for {project.DisplayName} — the content went straight to the"
            + $" Merge Gate as a {kind} candidate.";
    }
}
