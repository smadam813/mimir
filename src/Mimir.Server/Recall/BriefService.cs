using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// The Brief (§7): the compact, project-aware Wisdom injection delivered at session start. No
/// query exists yet, so rank is <c>brief_score</c> over the ambient Candidate Universe — the
/// session's Project plus Global, non-Retired, minus Wisdom the built-in already loads natively.
/// Storage owns that universe (<see cref="WisdomSearch.ListAmbientAsync"/>) for every lane, so
/// this one lists its ids and hydrates them, exactly as the query lanes do. Every actual injection
/// logs an Injection row; an empty decision leaves no trace (§7).
/// </summary>
internal sealed class BriefService(
    MimirDbContext db,
    WisdomSearch search,
    IOptions<RecallOptions> options,
    TimeProvider clock)
{
    public async Task<string> ComposeBriefAsync(
        string sessionId, Guid projectId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var ids = await search.ListAmbientAsync(projectId, cancellationToken);
        // Hydration is where every rendered field comes from, so a Wisdom hard-deleted (§8)
        // between the listing and this query simply yields no row and never renders — the same
        // drop QueryRanking makes explicit against its own hits. Eligibility is not re-tested
        // here: a Wisdom Retired inside that same window still renders, because re-checking
        // would put a second keeper of a universe predicate back in Recall. The Prompt lane
        // holds the identical window off the same reasoning.
        var candidates = await db.Wisdom
            .Where(w => ids.Contains(w.Id))
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
