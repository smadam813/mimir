using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

internal static class AmbientUniverse
{
    internal static IQueryable<Wisdom> For(MimirDbContext db, Guid projectId, WisdomLens lens)
    {
        var universe = db.Wisdom.Where(
            w => w.ScopeProjectId == projectId || w.ScopeProjectId == Project.GlobalId);
        var live = universe.Where(w => w.RetiredAt == null);

        return lens switch
        {
            WisdomLens.Contested => live.Where(w => w.ContestedAt != null),
            WisdomLens.Orphaned => live.Where(w => !db.Provenance.Any(p => p.WisdomId == w.Id)),
            WisdomLens.Retired => universe.Where(w => w.RetiredAt != null),
            _ => live,
        };
    }
}
