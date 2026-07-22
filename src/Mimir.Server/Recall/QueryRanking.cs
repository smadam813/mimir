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
/// threshold (null off that leg, per the §3 score-scale rule), and what rendering needs.
/// </summary>
internal sealed record RankedWisdom(
    Guid WisdomId,
    double Score,
    double? Cosine,
    WisdomKind Kind,
    Guid ScopeProjectId,
    string Text,
    DateTimeOffset LastConfirmedAt)
{
    public InjectionEntry ToInjectionEntry()
        => new(WisdomId, Score, Kind, ScopeProjectId == Project.GlobalId, LastConfirmedAt, Text);
}

/// <summary>
/// The §7 query ranking as a shared service — the Prompt lane now, the Wisdom leg of
/// <c>mimir_search</c> and the golden runner later. Embeds the query, runs the §3 hybrid search,
/// and orders every hit by <see cref="RecallScoring.QueryScore"/> under the caller's affinity
/// context. Deliberately unthresholded and scope-unfiltered: consumers own their gates and
/// candidate universes.
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
    {
        var embedding = new Vector(
            await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken));
        var hits = await search.SearchAsync(embedding, query, cancellationToken);
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
                // Explicit salience (§7): any Provenance Event born from a deliberate save.
                Salient = db.Provenance.Any(p => p.WisdomId == w.Id && p.EventId != null
                    && db.Events.Any(e => e.Id == p.EventId && e.Salient)),
            })
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        var now = clock.GetUtcNow();
        return hits
            .Select(hit =>
            {
                var w = records[hit.WisdomId];
                return new RankedWisdom(
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
                    w.LastConfirmedAt);
            })
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.WisdomId)
            .ToList();
    }
}
