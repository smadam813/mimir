using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// The ambient candidate universe (§7), shared by both ambient lanes: the session's Project plus
/// Global, non-Retired — other Projects' Wisdom reaches here only via merge-gate promotion, never
/// directly — minus the native-content exclusion.
/// </summary>
internal static class AmbientCandidates
{
    public static IQueryable<Wisdom> Of(MimirDbContext db, Guid projectId)
        => db.Wisdom
            .Where(w => w.RetiredAt == null)
            .Where(w => w.ScopeProjectId == projectId || w.ScopeProjectId == Project.GlobalId)
            // Native-content exclusion (§7): Wisdom whose only Provenance is HarvestedItems of
            // the current Project never injects ambiently — the built-in already loads that
            // content. Orphaned provenance is not harvest-only, so it stays in.
            .Where(w => !db.Provenance.Any(p => p.WisdomId == w.Id)
                || db.Provenance.Any(p => p.WisdomId == w.Id
                    && (p.HarvestedItemId == null
                        || !db.HarvestedItems.Any(h =>
                            h.Id == p.HarvestedItemId && h.ProjectId == projectId))));
}
