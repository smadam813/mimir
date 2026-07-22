using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// The Brief (§7): the compact, project-aware Wisdom injection delivered at session start. No
/// query exists yet, so rank is <c>brief_score</c> over the ambient candidate universe — the
/// session's Project plus Global, non-Retired, minus Wisdom the built-in already loads natively.
/// Every actual injection logs an Injection row; an empty decision leaves no trace (§7).
/// </summary>
internal sealed class BriefService(
    MimirDbContext db,
    IOptions<RecallOptions> options,
    TimeProvider clock)
{
    public async Task<string> ComposeBriefAsync(
        string sessionId, Guid projectId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var candidates = await AmbientCandidates.Of(db, projectId)
            .Select(w => new
            {
                w.Id,
                w.Kind,
                w.ScopeProjectId,
                w.Text,
                w.Reinforcement,
                w.LastConfirmedAt,
                Salient = ExplicitSalience.Ids(db).Contains(w.Id),
            })
            .ToListAsync(cancellationToken);

        var entries = candidates
            .Select(c => new InjectionEntry(
                c.Id,
                RecallScoring.BriefScore(
                    c.Reinforcement, c.Salient, c.LastConfirmedAt, now, options.Value),
                c.Kind,
                c.ScopeProjectId == Project.GlobalId,
                c.LastConfirmedAt,
                c.Text))
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.WisdomId)
            .ToList();

        var (brief, included) = InjectionRenderer.Render(entries, options.Value.BriefBudgetChars);
        if (included.Count == 0)
        {
            return "";
        }

        InjectionLog.Record(
            db, sessionId, projectId, now, InjectionLane.Brief,
            queryContext: null, brief, included);
        await db.SaveChangesAsync(cancellationToken);
        return brief;
    }
}
