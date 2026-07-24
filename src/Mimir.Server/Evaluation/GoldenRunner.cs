using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;

namespace Mimir.Server.Evaluation;

/// <summary>
/// One GoldenCase's outcome: where the expected Wisdom actually ranked (1-based; null when it
/// did not rank at all) against the §9 golden-set k.
/// </summary>
public sealed record GoldenResult(
    Guid CaseId,
    string QueryContext,
    string Note,
    Guid ExpectedWisdomId,
    int? Rank,
    bool Passed);

/// <summary>The §9 golden-set measure: every case's outcome and the pass rate over them.</summary>
public sealed record GoldenReport(IReadOnlyList<GoldenResult> Results)
{
    public int PassedCount => Results.Count(r => r.Passed);

    /// <summary>Passed over run; 1.0 for an empty suite — no case has failed.</summary>
    public double PassRate => Results.Count == 0 ? 1.0 : (double)PassedCount / Results.Count;
}

/// <summary>
/// The §9 golden runner: replays every GoldenCase — promoted and hand-inserted alike — through
/// the shared §7 query ranking, unthresholded, under the case's own affinity context, and scores
/// each on whether its expected Wisdom ranks within the §11 golden-set k. Dev-time only: the
/// golden suite test is its one consumer, so it carries no registration.
/// </summary>
internal sealed class GoldenRunner(
    MimirDbContext db,
    QueryRanking ranking,
    IOptions<SearchOptions> options)
{
    public async Task<GoldenReport> RunAsync(CancellationToken cancellationToken)
    {
        var cases = await db.GoldenCases.AsNoTracking()
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);

        // Cases often share a query — repeated promotions of one query_context, or a hand-built
        // suite around one scenario — and a ranking is pure given (query, affinity project), so
        // each distinct pair embeds and searches once instead of per case. Sequential on purpose:
        // the runner and the ranking share one context.
        var rankings = new Dictionary<(string Query, Guid ProjectId), IReadOnlyList<RankedWisdom>>();
        var results = new List<GoldenResult>(cases.Count);
        foreach (var goldenCase in cases)
        {
            var key = (goldenCase.QueryContext, goldenCase.ProjectId);
            if (!rankings.TryGetValue(key, out var ranked))
            {
                // §9 fixes the affinity context, not the universe: the runner ranks the whole
                // tier, and says so, because narrowing it to the ambient universe would move the
                // pass rate for reasons that are not the ranking's.
                ranked = await ranking.RankEverythingAsync(
                    goldenCase.QueryContext,
                    goldenCase.ProjectId,
                    WisdomSearchFilter.None,
                    cancellationToken);
                rankings[key] = ranked;
            }
            int? rank = null;
            for (var i = 0; i < ranked.Count; i++)
            {
                if (ranked[i].WisdomId == goldenCase.ExpectedWisdomId)
                {
                    rank = i + 1;
                    break;
                }
            }

            results.Add(new GoldenResult(
                goldenCase.Id,
                goldenCase.QueryContext,
                goldenCase.Note,
                goldenCase.ExpectedWisdomId,
                rank,
                // The lifted <= holds a null rank (never ranked at all) to a fail.
                rank <= options.Value.GoldenSetK));
        }

        return new GoldenReport(results);
    }
}
