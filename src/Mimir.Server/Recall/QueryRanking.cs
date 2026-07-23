using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Pgvector;

namespace Mimir.Server.Recall;

/// <summary>
/// One query-ranked Wisdom: the §7 score that ordered it, the vector leg's cosine for the caller's
/// threshold (null off that leg, per the §3 score-scale rule), what rendering needs, and whether
/// the row sits in the affinity context's ambient candidate universe — an annotation, not a
/// filter, so consumers that reach everything (<c>mimir_search</c>) can ignore it.
/// </summary>
internal sealed record RankedWisdom(
    Guid WisdomId,
    double Score,
    double? Cosine,
    WisdomKind Kind,
    Guid ScopeProjectId,
    string Text,
    DateTimeOffset LastConfirmedAt,
    DateTimeOffset? RetiredAt,
    bool AmbientEligible)
{
    public InjectionEntry ToInjectionEntry()
        => new(WisdomId, Score, Kind, ScopeProjectId == Project.GlobalId, LastConfirmedAt, Text);
}

/// <summary>
/// The §7 query ranking as a shared service — the Prompt lane now, the Wisdom leg of
/// <c>mimir_search</c> and the golden runner later. Embeds the query, runs the §3 hybrid search,
/// and orders every hit by <see cref="RecallScoring.QueryScore"/> under the caller's affinity
/// context. Deliberately unthresholded and scope-unfiltered: consumers own their gates and
/// candidate universes — ambient eligibility rides along as a flag so the Prompt lane needs no
/// second query to apply its own.
/// </summary>
internal sealed class QueryRanking(
    MimirDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    WisdomSearch search,
    IOptions<RecallOptions> options,
    TimeProvider clock)
{
    /// <param name="affinityProjectId">The Project whose Wisdom earns the affinity boost —
    /// the session's Project for the ambient lane, the case's for the golden runner.</param>
    public async Task<IReadOnlyList<RankedWisdom>> RankAsync(
        string query, Guid affinityProjectId, CancellationToken cancellationToken)
        => await RankAsync(query, affinityProjectId, WisdomSearchFilter.None, cancellationToken);

    /// <param name="filter">The <c>mimir_search</c> narrowings, pushed into the §3 search's SQL so
    /// they apply before the per-leg top-N — a filtered rank is over the whole corpus, never the
    /// filtered residue of an unfiltered pool. Every other consumer takes the overload above.</param>
    public async Task<IReadOnlyList<RankedWisdom>> RankAsync(
        string query, Guid affinityProjectId, WisdomSearchFilter filter, CancellationToken cancellationToken)
    {
        var embedding = new Vector(
            await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken));
        var hits = await search.SearchAsync(embedding, query, filter, cancellationToken);
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
                AmbientEligible = AmbientCandidates.Of(db, affinityProjectId)
                    .Any(c => c.Id == w.Id),
            })
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        var now = clock.GetUtcNow();
        var ranked = new List<RankedWisdom>(hits.Count);
        foreach (var hit in hits)
        {
            // A Wisdom hard-deleted (§8) between the search and the hydration query drops out;
            // consumers must never meet an id the row for which no longer exists.
            if (!records.TryGetValue(hit.WisdomId, out var w))
            {
                continue;
            }

            ranked.Add(new RankedWisdom(
                w.Id,
                RecallScoring.QueryScore(
                    hit.FusedScore,
                    // Global Wisdom never earns affinity, even under a Global context (§7).
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
                w.RetiredAt,
                w.AmbientEligible));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.WisdomId)
            .ToList();
    }
}
