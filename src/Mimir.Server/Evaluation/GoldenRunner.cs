using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;

namespace Mimir.Server.Evaluation;

public sealed record GoldenResult(
    Guid CaseId,
    string QueryContext,
    string Note,
    Guid ExpectedWisdomId,
    int? Rank,
    bool Passed);

public sealed record GoldenReport(IReadOnlyList<GoldenResult> Results)
{
    public int PassedCount => Results.Count(r => r.Passed);

    public double PassRate => Results.Count == 0 ? 1.0 : (double)PassedCount / Results.Count;
}

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

        var rankings = new Dictionary<(string Query, Guid ProjectId), IReadOnlyList<RankedWisdom>>();
        var results = new List<GoldenResult>(cases.Count);
        foreach (var goldenCase in cases)
        {
            var key = (goldenCase.QueryContext, goldenCase.ProjectId);
            if (!rankings.TryGetValue(key, out var ranked))
            {
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
                rank <= options.Value.GoldenSetK));
        }

        return new GoldenReport(results);
    }
}
