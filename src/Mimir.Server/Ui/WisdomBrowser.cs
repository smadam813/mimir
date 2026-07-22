using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Pgvector;

namespace Mimir.Server.Ui;

/// <summary>The §8.1 scope filter: everything, Global only, or Project-scoped only.</summary>
public enum WisdomScopeFilter
{
    Any,
    Global,
    ProjectScoped,
}

/// <summary>
/// The §8.1 retired filter. <see cref="Active"/> is the default-search rule (§10: Retired Wisdom
/// is excluded from default search); the other values are how the browser reaches it anyway.
/// </summary>
public enum WisdomRetirementFilter
{
    Active,
    Retired,
    All,
}

/// <summary>
/// One §8.1 browser query: free-text search plus the five filters. The contested filter is the
/// adjudication review surface (§8.4); defaults mean "everything active, newest first".
/// </summary>
public sealed record WisdomQuery(
    string? Search = null,
    WisdomKind? Kind = null,
    WisdomScopeFilter Scope = WisdomScopeFilter.Any,
    Guid? ProjectId = null,
    bool ContestedOnly = false,
    WisdomRetirementFilter Retirement = WisdomRetirementFilter.Active);

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
/// The read-and-curate surface behind the Wisdom browser (§8.1). Every method opens its own
/// short-lived context, like <see cref="EpisodeBrowser"/>. Curation edits Wisdom in place — the
/// Merge Gate stays the only entry point for *new* Wisdom (§6) — and edit is the one UI action
/// that talks to a model: the new text must re-embed or search would keep finding the old words.
/// </summary>
public sealed class WisdomBrowser(
    IDbContextFactory<MimirDbContext> contexts,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<WisdomListEntry>> ListAsync(
        WisdomQuery query, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var wisdom = db.Wisdom.AsQueryable();

        wisdom = query.Retirement switch
        {
            WisdomRetirementFilter.Active => wisdom.Where(w => w.RetiredAt == null),
            WisdomRetirementFilter.Retired => wisdom.Where(w => w.RetiredAt != null),
            _ => wisdom,
        };
        if (query.Kind is { } kind)
        {
            wisdom = wisdom.Where(w => w.Kind == kind);
        }

        wisdom = query.Scope switch
        {
            WisdomScopeFilter.Global => wisdom.Where(w => w.ScopeProjectId == Project.GlobalId),
            WisdomScopeFilter.ProjectScoped => wisdom.Where(w => w.ScopeProjectId != Project.GlobalId),
            _ => wisdom,
        };
        if (query.ProjectId is { } projectId)
        {
            wisdom = wisdom.Where(w => w.ScopeProjectId == projectId);
        }

        if (query.ContestedOnly)
        {
            wisdom = wisdom.Where(w => w.ContestedAt != null);
        }

        if (query.Search?.Trim() is { Length: > 0 } term)
        {
            // Word-aware FTS over the generated tsv, with a substring fallback so partial words
            // still find their Wisdom — a browser search, not the §3 ranked hybrid search.
            var pattern = "%" + term
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_") + "%";
            wisdom = wisdom.Where(w =>
                w.Tsv!.Matches(EF.Functions.PlainToTsQuery("english", term))
                || EF.Functions.ILike(w.Text, pattern, @"\"));
        }

        return await ToEntries(db, wisdom.OrderByDescending(w => w.LastConfirmedAt).ThenBy(w => w.Id))
            .ToListAsync(cancellationToken);
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
    /// The §8.1 edit: the new text becomes current — re-embedded, appended to the chain as a
    /// <c>cause=edited</c> WisdomVersion. Reinforcement and recency are untouched: an edit
    /// rewords, only the Merge Gate confirms (§6). An unchanged text is a no-op.
    /// </summary>
    public async Task EditAsync(Guid wisdomId, string text, CancellationToken cancellationToken)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var wisdom = await db.Wisdom.FirstOrDefaultAsync(w => w.Id == wisdomId, cancellationToken);
        if (wisdom is null || wisdom.Text == trimmed)
        {
            return;
        }

        wisdom.Text = trimmed;
        wisdom.Embedding = new Vector(
            await embeddings.GenerateVectorAsync(trimmed, cancellationToken: cancellationToken));

        var latest = await db.WisdomVersions
            .Where(v => v.WisdomId == wisdomId)
            .MaxAsync(v => (int?)v.Version, cancellationToken) ?? 0;
        db.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = wisdomId,
            Version = latest + 1,
            Text = trimmed,
            CreatedAt = clock.GetUtcNow(),
            Cause = WisdomVersionCause.Edited,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

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
