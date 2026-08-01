using System.Text;
using System.Text.RegularExpressions;
using Mimir.Contracts.Mcp;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

internal sealed partial class McpSearchService(
    QueryRanking ranking,
    EventSearch events,
    McpProjects projects,
    InjectionLog injections)
{
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

        var requester = await projects.FindRequesterAsync(
            request.ProjectIdentity, request.ProjectRoot, cancellationToken);
        var affinityProjectId = requester?.Id ?? Project.GlobalId;

        // Npgsql refuses a non-UTC DateTimeOffset against timestamptz.
        var since = request.Since?.ToUniversalTime();

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
                var retired = w.RetiredAt is { } at ? $" · Retired {InjectionLabel.Date(at)}" : "";
                text.Append(InjectionLabel.Line(w.Kind, scope, w.LastConfirmedAt, w.Text, retired));
            }
        }

        if (eventHits.Count > 0)
        {
            text.Append($"\nEpisode events ({eventHits.Count}):\n");
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

    private static string Snippet(string payload)
    {
        var collapsed = Whitespace().Replace(payload, " ").Trim();
        return collapsed.Length <= SnippetChars ? collapsed : collapsed[..SnippetChars] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
