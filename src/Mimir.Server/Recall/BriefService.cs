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
/// this one lists its ids and hydrates them, exactly as the query lanes do. Rendering and the §7
/// recording rules belong to <see cref="InjectionLog"/>; this lane decides what to hand it and
/// records the session's Project. Nothing in that chain is bounded by anything but the corpus,
/// which is why every composition measures itself against <see cref="BriefTripwire"/>.
/// </summary>
internal sealed class BriefService(
    MimirDbContext db,
    WisdomSearch search,
    InjectionLog injections,
    IOptions<RecallOptions> options,
    TimeProvider clock,
    ILogger<BriefService> logger)
{
    public async Task<string> ComposeBriefAsync(
        string sessionId, Guid projectId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        var ids = await search.ListAmbientAsync(projectId, cancellationToken);
        // Hydration is where every rendered field comes from, so a Wisdom hard-deleted (§8)
        // between the listing and this query simply yields no row and never renders — the same
        // drop QueryRanking makes explicit against its own hits. Retirement in that same window
        // needs saying, because it leaves the row in place: the guard here is not a second
        // keeper of the universe (scope and the native-content exclusion stay Storage's alone),
        // it is the §7 rule that no lane may ever render a Retired row, re-asserted at the last
        // read before rendering — the same one-predicate re-check InjectionBrowser makes when it
        // hydrates ids of its own.
        var candidates = await db.Wisdom
            .Where(w => ids.Contains(w.Id) && w.RetiredAt == null)
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

        // Measured before rendering: everything that grows with the corpus has happened by here,
        // and the render is bounded by the budget it is about to be handed.
        var notice = BriefTripwire.Fire(logger, clock.GetElapsedTime(started), candidates.Count);

        // The keeper renders, applies the §7 empty-trace rule and logs the row. A tripwire line
        // still comes back out of a Brief that had no Wisdom to log — a degraded compose that
        // returned "" would look exactly like a healthy Brief with nothing to say, which is the
        // confusion the tripwire exists to prevent.
        return await injections.RenderAndRecordAsync(
            new InjectionContext(InjectionLane.Brief, sessionId, projectId, QueryContext: null),
            entries,
            options.Value.BriefBudgetChars,
            notice,
            cancellationToken);
    }
}
