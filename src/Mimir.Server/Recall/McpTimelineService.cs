using Microsoft.EntityFrameworkCore;
using Mimir.Contracts.Mcp;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// <c>mimir_timeline</c> (§7): the Episode timeline, newest first, every Project unless narrowed.
/// Each entry carries its seal state — live, or sealed with the §4 reason — so a session can see
/// what is still open. Nothing here is Wisdom, so nothing is logged as an Injection.
/// </summary>
internal sealed class McpTimelineService(MimirDbContext db, McpProjects projects)
{
    private const int MaxEpisodes = 20;

    public async Task<string> TimelineAsync(McpTimelineRequest request, CancellationToken cancellationToken)
    {
        var (filter, miss) = await projects.ResolveFilterAsync(request.Project, cancellationToken);
        if (miss is not null)
        {
            return miss;
        }

        // Npgsql refuses a non-UTC DateTimeOffset against timestamptz; the CLI normalizes, but
        // the endpoint is open to any local client.
        var since = request.Since?.ToUniversalTime();
        var episodes = await db.Episodes
            .Where(e => filter == null || e.ProjectId == filter.Id)
            .Where(e => since == null || e.StartedAt >= since)
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Take(MaxEpisodes)
            .Select(e => new
            {
                e.SessionId,
                e.ProjectId,
                e.StartedAt,
                e.SealedAt,
                e.SealReason,
                Events = db.Events.Count(ev => ev.EpisodeId == e.Id),
            })
            .ToListAsync(cancellationToken);

        if (episodes.Count == 0)
        {
            return "No Episodes match.";
        }

        var names = await projects.DisplayNamesAsync(
            episodes.Select(e => e.ProjectId), cancellationToken);
        var lines = episodes.Select(e =>
        {
            var seal = McpTexts.SealState(e.SealedAt, e.SealReason);
            var project = names.GetValueOrDefault(e.ProjectId, McpTexts.UnknownProject);
            return $"- {e.SessionId} · {project} · started {McpTexts.Timestamp(e.StartedAt)}"
                + $" · {e.Events} events · {seal}";
        });

        return $"Episodes ({episodes.Count}, newest first):\n" + string.Join('\n', lines);
    }
}
