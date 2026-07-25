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
/// results are two ranked sections of one answer, not one interleaved list. This lane composes its
/// own answer rather than the ambient wrapper, and hands it to <see cref="InjectionLog"/> to record
/// (lane=MCP, the query as <c>query_context</c>, the affinity Project). The replies that answer
/// without recalling anything — an unknown kind, an unresolvable Project filter, a query nothing
/// matched — return before the keeper: they are this lane's own wording, not an injection, and §7
/// leaves no trace of them.
/// </summary>
internal sealed partial class McpSearchService(
    QueryRanking ranking,
    EventSearch events,
    McpProjects projects,
    InjectionLog injections)
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

        // Npgsql refuses a non-UTC DateTimeOffset against timestamptz; the CLI normalizes, but
        // the endpoint is open to any local client.
        var since = request.Since?.ToUniversalTime();

        // Both legs filter in SQL, before their LIMIT — a narrow filter over a large corpus
        // finds deep matches instead of emptying an unfiltered top-N pool.
        var ranked = await ranking.RankEverythingAsync(
            request.Query,
            affinityProjectId,
            new WisdomSearchFilter
            {
                IncludeRetired = request.IncludeRetired,
                Kind = kind,
                ScopeProjectId = filter?.Id,
                Since = since,
            },
            cancellationToken);
        var wisdom = ranked.Take(MaxWisdom).ToList();

        IReadOnlyList<EventSearchHit> eventHits = request.IncludeEpisodes
            ? await events.SearchAsync(
                request.Query, filter?.Id, since, MaxEventHits, cancellationToken)
            : [];

        if (wisdom.Count == 0 && eventHits.Count == 0)
        {
            return $"No Wisdom or Episode matches for \"{request.Query}\".";
        }

        var names = await projects.DisplayNamesAsync(
            wisdom.Select(w => w.ScopeProjectId).Concat(eventHits.Select(h => h.ProjectId)),
            cancellationToken);
        var text = Render(request.Query, wisdom, eventHits, names);

        // The affinity Project, not any Project in the answer: this lane reaches every scope, and
        // what the row records is the context the ranking boosted under (§7.1).
        await injections.RecordAsync(
            new InjectionContext(
                InjectionLane.Mcp, request.SessionId, affinityProjectId, request.Query),
            text,
            wisdom.Select(w => w.ToInjectionEntry()).ToList(),
            cancellationToken);
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
                // The shared §7 label line, with this surface's own scope wording and Retired tag —
                // the Retired date reads from the same builder, so one line cannot carry two
                // date rules.
                var retired = w.RetiredAt is { } at ? $" · Retired {InjectionLabel.Date(at)}" : "";
                text.Append(InjectionLabel.Line(w.Kind, scope, w.LastConfirmedAt, w.Text, retired));
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
