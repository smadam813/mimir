using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Pgvector;

namespace Mimir.Server.Recall;

internal sealed record RankedWisdom(
    Guid WisdomId,
    double Score,
    double? Cosine,
    WisdomKind Kind,
    Guid ScopeProjectId,
    string Text,
    DateTimeOffset LastConfirmedAt,
    DateTimeOffset? RetiredAt)
{
    public InjectionEntry ToInjectionEntry()
        => new(WisdomId, Score, Kind, ScopeProjectId == Project.GlobalId, LastConfirmedAt, Text);
}

internal sealed class QueryRanking(
    MimirDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    WisdomSearch search,
    IOptions<RecallOptions> options,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<RankedWisdom>> RankAmbientAsync(
        string query, Guid sessionProjectId, CancellationToken cancellationToken)
    {
        var embedding = await EmbedAsync(query, cancellationToken);
        var hits = await search.SearchAmbientAsync(
            embedding, query, sessionProjectId, cancellationToken);
        return await RankAsync(hits, sessionProjectId, cancellationToken);
    }

    public async Task<IReadOnlyList<RankedWisdom>> RankEverythingAsync(
        string query,
        Guid affinityProjectId,
        WisdomSearchFilter filter,
        CancellationToken cancellationToken)
    {
        var embedding = await EmbedAsync(query, cancellationToken);
        var hits = await search.SearchAsync(embedding, query, filter, cancellationToken);
        return await RankAsync(hits, affinityProjectId, cancellationToken);
    }

    private async Task<Vector> EmbedAsync(string query, CancellationToken cancellationToken)
        => new(await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken));

    private async Task<IReadOnlyList<RankedWisdom>> RankAsync(
        IReadOnlyList<WisdomSearchHit> hits,
        Guid affinityProjectId,
        CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
        {
            return [];
        }

        var ids = hits.Select(h => h.WisdomId).ToList();
        var records = await db.Wisdom
            .Where(w => ids.Contains(w.Id))
            .Select(w => new
            {
                w.Id,
                w.Kind,
                w.ScopeProjectId,
                w.Text,
                w.Reinforcement,
                w.LastConfirmedAt,
                w.RetiredAt,
                Salient = ExplicitSalience.Ids(db).Contains(w.Id),
            })
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        var now = clock.GetUtcNow();
        var ranked = new List<RankedWisdom>(hits.Count);
        foreach (var hit in hits)
        {
            // Drops a Wisdom hard-deleted between the search and this query. Unforceable from a test.
            if (!records.TryGetValue(hit.WisdomId, out var w))
            {
                continue;
            }

            ranked.Add(new RankedWisdom(
                w.Id,
                RecallScoring.QueryScore(
                    hit.FusedScore,
                    w.ScopeProjectId != Project.GlobalId && w.ScopeProjectId == affinityProjectId,
                    w.Reinforcement,
                    w.Salient,
                    w.LastConfirmedAt,
                    now,
                    options.Value),
                hit.Cosine,
                w.Kind,
                w.ScopeProjectId,
                w.Text,
                w.LastConfirmedAt,
                w.RetiredAt));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.WisdomId)
            .ToList();
    }
}
