using System.Text;
using System.Text.RegularExpressions;
using Mimir.Contracts.Mcp;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// <c>mimir_search</c> (§7): deliberate recall over both tiers. The Wisdom leg runs the shared §7
/// query ranking — scope-unfiltered, so other Projects' Wisdom is reachable, with Retired rows
/// only on request; the Episode leg is FTS-only over <c>Event.tsv</c> plus metadata filters. The
/// two legs' scores are incommensurable (a §7 query score vs. a bare <c>ts_rank</c>), so "fused"
/// results are two ranked sections of one answer, not one interleaved list. Any non-empty answer
/// logs an Injection row (lane=MCP, the query as <c>query_context</c>); an empty one leaves no
/// trace, like every lane (§7).
/// </summary>
internal sealed partial class McpSearchService(
    MimirDbContext db,
    QueryRanking ranking,
    EventSearch events,
    McpProjects projects,
    TimeProvider clock)
{
    /// <summary>Rendering caps — deliberate recall wants the best few, not the §3 top-50 pool.</summary>
    private const int MaxWisdom = 10;

    private const int MaxEventHits = 10;

    private const int SnippetChars = 200;

    public async Task<string> SearchAsync(McpSearchRequest request, CancellationToken cancellationToken)
    {
        WisdomKind? kind = null;
        if (request.Kind is { Length: > 0 } kindText)
        {
            if (!Enum.TryParse<WisdomKind>(kindText, ignoreCase: true, out var parsed))
            {
                return McpTexts.UnknownKind(kindText);
            }

            kind = parsed;
        }

        var (filter, miss) = await projects.ResolveFilterAsync(request.Project, cancellationToken);
        if (miss is not null)
        {
            return miss;
        }

        // Unknown directory → no Project → the Global anchor, which earns no affinity boost.
        var requester = await projects.FindRequesterAsync(
            request.ProjectIdentity, request.ProjectRoot, cancellationToken);
        var affinityProjectId = requester?.Id ?? Project.GlobalId;

        // The Wisdom-leg filters apply after the ranking, whose pool the §3 search already bounded
        // to its per-leg top-N — a narrow filter over a large corpus can come back empty even
        // though deeper matches exist. The Prompt lane accepts the same crowding for v1; the §11
        // PerLegTopN knob widens the pool if it bites. (The Episode leg filters in SQL, pre-limit.)
        var ranked = await ranking.RankAsync(
            request.Query, affinityProjectId, request.IncludeRetired, cancellationToken);
        var wisdom = ranked
            .Where(r => kind is null || r.Kind == kind)
            .Where(r => filter is null || r.ScopeProjectId == filter.Id)
            .Where(r => request.Since is null || r.LastConfirmedAt >= request.Since)
            .Take(MaxWisdom)
            .ToList();

        IReadOnlyList<EventSearchHit> eventHits = request.IncludeEpisodes
            ? (await events.SearchAsync(request.Query, filter?.Id, request.Since, cancellationToken))
                .Take(MaxEventHits)
                .ToList()
            : [];

        if (wisdom.Count == 0 && eventHits.Count == 0)
        {
            return $"No Wisdom or Episode matches for \"{request.Query}\".";
        }

        var names = await projects.DisplayNamesAsync(
            wisdom.Select(w => w.ScopeProjectId).Concat(eventHits.Select(h => h.ProjectId)),
            cancellationToken);
        var text = Render(request.Query, wisdom, eventHits, names);

        InjectionLog.Record(
            db, request.SessionId, affinityProjectId, clock.GetUtcNow(), InjectionLane.Mcp,
            request.Query, text, wisdom.Select(w => w.ToInjectionEntry()).ToList());
        await db.SaveChangesAsync(cancellationToken);
        return text;
    }

    private static string Render(
        string query,
        IReadOnlyList<RankedWisdom> wisdom,
        IReadOnlyList<EventSearchHit> eventHits,
        IReadOnlyDictionary<Guid, string> names)
    {
        var text = new StringBuilder($"Mimir results for \"{query}\":\n");
        if (wisdom.Count > 0)
        {
            text.Append($"\nWisdom ({wisdom.Count}):\n");
            foreach (var w in wisdom)
            {
                var scope = w.ScopeProjectId == Project.GlobalId
                    ? "Global"
                    : names.GetValueOrDefault(w.ScopeProjectId, McpTexts.UnknownProject);
                var retired = w.RetiredAt is { } at ? $" · Retired {McpTexts.Date(at)}" : "";
                text.Append(
                    $"- [{w.Kind} · {scope} · confirmed {McpTexts.Date(w.LastConfirmedAt)}{retired}] {w.Text}\n");
            }
        }

        if (eventHits.Count > 0)
        {
            text.Append($"\nEpisode events ({eventHits.Count}):\n");
            // Grouped per Episode in first-hit order, so the best-ranked Episode leads.
            foreach (var episode in eventHits.GroupBy(h => h.EpisodeId))
            {
                var first = episode.First();
                var seal = McpTexts.SealState(first.SealedAt, first.SealReason);
                var project = names.GetValueOrDefault(first.ProjectId, McpTexts.UnknownProject);
                text.Append(
                    $"- Episode {first.SessionId} · {project} · started {McpTexts.Date(first.StartedAt)} · {seal}\n");
                foreach (var hit in episode)
                {
                    text.Append(
                        $"  · #{hit.Seq} {hit.Type} {McpTexts.Timestamp(hit.At)}: {Snippet(hit.Payload)}\n");
                }
            }
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>The stored payload JSON, whitespace-collapsed and clipped to a preview.</summary>
    private static string Snippet(string payload)
    {
        var collapsed = Whitespace().Replace(payload, " ").Trim();
        return collapsed.Length <= SnippetChars ? collapsed : collapsed[..SnippetChars] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
