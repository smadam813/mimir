using Mimir.Server.Storage;

namespace Mimir.Server.Recall;

internal static class ExplicitSalience
{
    public static IQueryable<Guid> Ids(MimirDbContext db)
        => db.Provenance
            .Where(p => p.EventId != null
                && db.Events.Any(e => e.Id == p.EventId && e.Salient))
            .Select(p => p.WisdomId);
}
