using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

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
        // Re-asserted: the listing cannot see a retirement landing between the two queries.
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

        var notice = BriefTripwire.Fire(logger, clock.GetElapsedTime(started), candidates.Count);

        return await injections.RenderAndRecordAsync(
            new InjectionContext(InjectionLane.Brief, sessionId, projectId, QueryContext: null),
            entries,
            options.Value.BriefBudgetChars,
            notice,
            cancellationToken);
    }
}
