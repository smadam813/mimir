using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// The §8 browser's Ambient Candidate Universe (ADR-0009): the selected Project's Wisdom plus
/// Global — what a session in that repository actually recalls, and what makes the per-row Scope
/// label mean something. One keeper for both consumers, the Wisdom listing and the sidebar's
/// "Needs attention" counts, so a count can never disagree with the list its own link opens.
/// Selecting Global needs no special case, since Global's ambient universe is itself.
/// </summary>
/// <remarks>
/// Deliberately <em>not</em> <see cref="WisdomSearch.ListAmbientAsync"/>, the Recall lanes' entry
/// point. The lanes' universe also drops Retired rows and applies §7's native-content exclusion; a
/// §8 curation surface has to be able to show both — the sidebar's Retired link exists to reach the
/// first, and harvest-derived Wisdom a curator cannot see is Wisdom they cannot retire. Same name,
/// same two scope arms, different surface.
/// </remarks>
internal static class AmbientUniverse
{
    /// <summary>
    /// That universe under one <paramref name="lens"/>. The three live lenses share one Retired
    /// predicate rather than restating it, since §10's "Retired is out of the default listing" is
    /// one rule and <see cref="WisdomLens.Retired"/> is the single deliberate exception to it.
    /// </summary>
    internal static IQueryable<Wisdom> For(MimirDbContext db, Guid projectId, WisdomLens lens)
    {
        var universe = db.Wisdom.Where(
            w => w.ScopeProjectId == projectId || w.ScopeProjectId == Project.GlobalId);
        var live = universe.Where(w => w.RetiredAt == null);

        return lens switch
        {
            WisdomLens.Contested => live.Where(w => w.ContestedAt != null),
            // The same rule WisdomBrowser.ToEntries flags OrphanedProvenance by: no Provenance
            // row at all (§3).
            WisdomLens.Orphaned => live.Where(w => !db.Provenance.Any(p => p.WisdomId == w.Id)),
            WisdomLens.Retired => universe.Where(w => w.RetiredAt != null),
            _ => live,
        };
    }
}
