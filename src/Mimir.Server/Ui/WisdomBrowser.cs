using Microsoft.EntityFrameworkCore;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// Which slice of the universe the listing is asking for — the sidebar's "Needs attention" group
/// is these four, one link each. <see cref="Active"/> is the default-listing rule (§10: Retired
/// Wisdom is out of default search, and the browser agrees); <see cref="Contested"/> is the
/// adjudication review surface (§8.4); <see cref="Orphaned"/> is the §3 state where every record a
/// Wisdom derived from was hard-deleted; <see cref="Retired"/> is how they are reached anyway.
/// </summary>
public enum WisdomLens
{
    Active,
    Contested,
    Orphaned,
    Retired,
}

/// <summary>
/// One §8.1 browser query. <paramref name="ProjectId"/> names a universe rather than narrowing one
/// (ADR-0009): the list is that Project's Ambient Candidate Universe — its own Wisdom plus Global —
/// which is what a session in that repository actually recalls. There is no scope filter, because
/// the sidebar selection has already said which universe this is and there is nothing left for a
/// second control to narrow.
/// </summary>
public sealed record WisdomQuery(
    Guid ProjectId,
    string? Search = null,
    WisdomKind? Kind = null,
    WisdomLens Lens = WisdomLens.Active);

/// <summary>One Kind chip: the Kind, and how much of the listed universe carries it.</summary>
public sealed record WisdomKindCount(WisdomKind Kind, int Count);

/// <summary>
/// One listing of the Ambient Candidate Universe. <see cref="ProjectOwned"/> and
/// <see cref="Global"/> partition <see cref="Entries"/> exactly, so the header can state the
/// arithmetic the sidebar's Project-owned counts otherwise leave as an inference (ADR-0009) —
/// selecting Global leaves <see cref="ProjectOwned"/> at zero, since Global's ambient universe is
/// itself. <see cref="Kinds"/> counts the same set <em>before</em> the Kind filter, so clicking a
/// chip never rewrites the other chips' numbers, and carries every Kind in the enum's own order —
/// a chip row that neither reshuffles nor drops the chip the curator has just clicked.
/// </summary>
public sealed record WisdomListing(
    IReadOnlyList<WisdomListEntry> Entries,
    int ProjectOwned,
    int Global,
    IReadOnlyList<WisdomKindCount> Kinds);

/// <summary>
/// One browser row (§8.1), self-describing enough for the reusable curation affordance: kind and
/// state badges, scope by name, and the orphaned-provenance flag (§3) ride along with the text.
/// </summary>
public sealed record WisdomListEntry(
    Guid Id,
    WisdomKind Kind,
    Guid ScopeProjectId,
    string ScopeName,
    string Text,
    int Reinforcement,
    DateTimeOffset LastConfirmedAt,
    DateTimeOffset? ContestedAt,
    DateTimeOffset? RetiredAt,
    Guid? SupersededBy,
    bool OrphanedProvenance);

/// <summary>
/// One Provenance row resolved for display (§8.1): ids to link with, plus the words a human
/// recognizes the referenced record by. Every referenced record still exists — hard deletes
/// cascade the Provenance rows that pointed at them (§3) — so the display fields are non-null
/// wherever the matching id is.
/// </summary>
public sealed record ProvenanceEntry(
    Guid Id,
    Guid? EpisodeId,
    Guid? EpisodeProjectId,
    string? SessionId,
    Guid? EventId,
    int? EventSeq,
    EventType? EventType,
    Guid? HarvestedItemId,
    string? HarvestedPath);

/// <summary>The §8.1 detail: current state, the full version chain (newest first), Provenance.</summary>
public sealed record WisdomDetail(
    WisdomListEntry Entry,
    IReadOnlyList<WisdomVersion> Versions,
    IReadOnlyList<ProvenanceEntry> Provenance);

/// <summary>
/// The read-and-curate surface behind the Wisdom browser (§8.1), listing one Project's
/// <see cref="AmbientUniverse"/> (ADR-0009). Every read opens its own
/// short-lived context, like <see cref="EpisodeBrowser"/>. Retire and delete are this class's own
/// writes — they change a row's standing, never its words. Edit does change the words, so it goes
/// through the Merge Gate (ADR-0004): the gate owns re-embedding, the version chain, and the lock
/// that keeps an interactive edit from colliding with a background rewrite. Internal, unlike its
/// sibling browsers, only because taking the internal <see cref="MergeGate"/> makes it so — the
/// Blazor components that inject it live in this assembly.
/// </summary>
internal sealed class WisdomBrowser(
    IDbContextFactory<MimirDbContext> contexts,
    MergeGate gate,
    TimeProvider clock)
{
    public async Task<WisdomListing> ListAsync(WisdomQuery query, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        // One read of the universe, and every figure the header renders taken off the rows it
        // returned. Separate counting queries would each see their own snapshot, so an Admission
        // landing between them leaves the chips and the header disagreeing with the list beside
        // them; a curator reading "Rule 4" over three rows has no way to tell which number lied.
        // That costs the Kind chips their narrower SQL — the "All" chip already reads this whole
        // set, so it is the price the default listing pays anyway.
        var chipped = await ToEntries(
                db,
                Search(AmbientUniverse.For(db, query.ProjectId, query.Lens), query.Search)
                    .OrderByDescending(w => w.LastConfirmedAt).ThenBy(w => w.Id))
            .ToListAsync(cancellationToken);

        // The chips count before the Kind filter, so clicking one never rewrites the others.
        var counted = chipped.CountBy(w => w.Kind).ToDictionary(c => c.Key, c => c.Value);
        var entries = query.Kind is { } kind ? chipped.Where(w => w.Kind == kind).ToList() : chipped;
        var global = entries.Count(w => w.ScopeProjectId == Project.GlobalId);

        return new WisdomListing(
            entries,
            entries.Count - global,
            global,
            [.. Enum.GetValues<WisdomKind>().Select(
                kind => new WisdomKindCount(kind, counted.GetValueOrDefault(kind)))]);
    }

    private static IQueryable<Wisdom> Search(IQueryable<Wisdom> wisdom, string? search)
    {
        if (search?.Trim() is not { Length: > 0 } term)
        {
            return wisdom;
        }

        // Word-aware FTS over the generated tsv, with a substring fallback so partial words
        // still find their Wisdom — a browser search, not the §3 ranked hybrid search.
        var pattern = "%" + term
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_") + "%";
        return wisdom.Where(w =>
            w.Tsv!.Matches(EF.Functions.PlainToTsQuery("english", term))
            || EF.Functions.ILike(w.Text, pattern, @"\"));
    }

    public async Task<WisdomDetail?> GetAsync(Guid wisdomId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entry = await ToEntries(db, db.Wisdom.Where(w => w.Id == wisdomId))
            .FirstOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var versions = await db.WisdomVersions.AsNoTracking()
            .Where(v => v.WisdomId == wisdomId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(cancellationToken);

        // The drill-down resolves each link to the words a human recognizes it by. An Event link
        // fills the Episode side from the Event's own Episode when the row carries none.
        var provenance = await db.Provenance
            .Where(p => p.WisdomId == wisdomId)
            .OrderBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.HarvestedItemId,
                Path = db.HarvestedItems
                    .Where(h => h.Id == p.HarvestedItemId).Select(h => h.Path).FirstOrDefault(),
                p.EventId,
                Event = db.Events
                    .Where(e => e.Id == p.EventId)
                    .Select(e => new { e.Seq, e.Type, e.EpisodeId }).FirstOrDefault(),
                EpisodeId = p.EpisodeId
                    ?? db.Events.Where(e => e.Id == p.EventId).Select(e => (Guid?)e.EpisodeId).FirstOrDefault(),
            })
            .Select(p => new ProvenanceEntry(
                p.Id,
                p.EpisodeId,
                db.Episodes.Where(e => e.Id == p.EpisodeId).Select(e => (Guid?)e.ProjectId).FirstOrDefault(),
                db.Episodes.Where(e => e.Id == p.EpisodeId).Select(e => e.SessionId).FirstOrDefault(),
                p.EventId,
                p.Event != null ? p.Event.Seq : null,
                p.Event != null ? p.Event.Type : null,
                p.HarvestedItemId,
                p.Path))
            .ToListAsync(cancellationToken);

        return new WisdomDetail(entry, versions, provenance);
    }

    /// <summary>
    /// The one projection every surface reads a Wisdom through — the browser's listing and
    /// detail, and the injection log's items, so curation affordances follow Wisdom everywhere
    /// it renders (§8).
    /// </summary>
    internal static IQueryable<WisdomListEntry> ToEntries(MimirDbContext db, IQueryable<Wisdom> wisdom)
        => wisdom.Select(w => new WisdomListEntry(
            w.Id,
            w.Kind,
            w.ScopeProjectId,
            db.Projects.Where(p => p.Id == w.ScopeProjectId).Select(p => p.DisplayName).First(),
            w.Text,
            w.Reinforcement,
            w.LastConfirmedAt,
            w.ContestedAt,
            w.RetiredAt,
            w.SupersededBy,
            !db.Provenance.Any(p => p.WisdomId == w.Id)));

    /// <summary>
    /// The §8.1 edit, handed to the Merge Gate: the new text becomes current — re-embedded,
    /// appended to the chain as a <c>cause=edited</c> WisdomVersion — while Reinforcement and
    /// recency stay put, since an edit rewords and only the gate's Admissions confirm (§6). An
    /// unchanged text is a no-op; a Retired one is not, since Retire changes a row's standing and
    /// not its words — see the gate for the full set. The edit can wait behind an in-flight
    /// Admission batch, the same acceptance <c>mimir_remember</c> makes.
    /// </summary>
    public async Task EditAsync(Guid wisdomId, string text, CancellationToken cancellationToken)
        => await gate.EditAsync(wisdomId, text, cancellationToken);

    /// <summary>§10 Retire: reversibly out of all recall and default search from this moment.</summary>
    public async Task RetireAsync(Guid wisdomId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Wisdom.Where(w => w.Id == wisdomId && w.RetiredAt == null)
            .ExecuteUpdateAsync(
                w => w.SetProperty(x => x.RetiredAt, clock.GetUtcNow()), cancellationToken);
    }

    public async Task UnretireAsync(Guid wisdomId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Wisdom.Where(w => w.Id == wisdomId)
            .ExecuteUpdateAsync(
                w => w.SetProperty(x => x.RetiredAt, (DateTimeOffset?)null), cancellationToken);
    }

    /// <summary>
    /// The §10 permanent act: the row goes and the schema cascades the version chain and the
    /// Provenance with it. Confirmation is the UI's job; this method is the point of no return.
    /// </summary>
    public async Task DeleteAsync(Guid wisdomId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Wisdom.Where(w => w.Id == wisdomId).ExecuteDeleteAsync(cancellationToken);
    }
}
