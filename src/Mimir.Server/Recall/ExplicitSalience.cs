using Mimir.Server.Storage;

namespace Mimir.Server.Recall;

/// <summary>
/// Explicit salience (§7): a Wisdom is salient when any of its Provenance Events was born from a
/// deliberate save. One definition, composed into every candidate query that scores salience —
/// the §7 boost must never diverge between the Brief and the Prompt lane.
/// </summary>
internal static class ExplicitSalience
{
    public static IQueryable<Guid> Ids(MimirDbContext db)
        => db.Provenance
            .Where(p => p.EventId != null
                && db.Events.Any(e => e.Id == p.EventId && e.Salient))
            .Select(p => p.WisdomId);
}
