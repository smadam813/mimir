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
/// threshold (null off that leg, per the §3 score-scale rule), and what rendering needs. No
/// eligibility annotation rides along — the Candidate Universe is named by the ranking method the
/// caller chose, so every row here is already inside it.
/// </summary>
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

/// <summary>
/// The §7 query ranking as a shared service — the Prompt lane, the Wisdom leg of
/// <c>mimir_search</c>, and the golden runner. Embeds the query, runs the §3 hybrid search, and
/// orders every hit by <see cref="RecallScoring.QueryScore"/> under the caller's affinity context.
/// Deliberately unthresholded: consumers own their gates. The Candidate Universe is not theirs to
/// own — each method names the universe it ranks, and the search restricts to it, so no consumer
/// can forget a filter that was never its to apply.
/// </summary>
internal sealed class QueryRanking(
    MimirDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    WisdomSearch search,
    IOptions<RecallOptions> options,
    TimeProvider clock)
{
    /// <summary>
    /// Ranks the ambient Candidate Universe (§7) — the session's Project plus Global, non-Retired,
    /// minus the native-content exclusion — restricted inside both search legs, before their
    /// per-leg top-N, so a nearer foreign corpus can never crowd an eligible match out of the pool.
    /// </summary>
    /// <param name="sessionProjectId">The session's Project: both the universe ranked and the
    /// affinity context ranked under — always the same value in the ambient lane.</param>
    public async Task<IReadOnlyList<RankedWisdom>> RankAmbientAsync(
        string query, Guid sessionProjectId, CancellationToken cancellationToken)
        => await RankAsync(
            query,
            sessionProjectId,
            new WisdomSearchFilter { AmbientProjectId = sessionProjectId },
            cancellationToken);

    /// <summary>
    /// Ranks every Project's Wisdom, narrowed only by <paramref name="filter"/> — the
    /// <c>mimir_search</c> universe. There is no unfiltered overload: reaching past the ambient
    /// universe is stated, never defaulted, so the golden runner passes
    /// <see cref="WisdomSearchFilter.None"/> to say it means the whole tier.
    /// </summary>
    /// <param name="affinityProjectId">The Project whose Wisdom earns the affinity boost — the
    /// requester's for <c>mimir_search</c>, the case's for the golden runner.</param>
    /// <param name="filter">The narrowings, pushed into the §3 search's SQL so they apply before
    /// the per-leg top-N — a filtered rank is over the whole corpus, never the filtered residue of
    /// an unfiltered pool.</param>
    public async Task<IReadOnlyList<RankedWisdom>> RankEverythingAsync(
        string query,
        Guid affinityProjectId,
        WisdomSearchFilter filter,
        CancellationToken cancellationToken)
    {
        // The method name is the universe in both directions: no caller may reach the ambient
        // universe without saying so, and none may smuggle it past a name that says everything.
        if (filter.AmbientProjectId is not null)
        {
            throw new ArgumentException(
                $"{nameof(WisdomSearchFilter.AmbientProjectId)} names a universe this method does " +
                $"not rank; call {nameof(RankAmbientAsync)} instead.",
                nameof(filter));
        }

        return await RankAsync(query, affinityProjectId, filter, cancellationToken);
    }

    private async Task<IReadOnlyList<RankedWisdom>> RankAsync(
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
                w.RetiredAt));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.WisdomId)
            .ToList();
    }
}
