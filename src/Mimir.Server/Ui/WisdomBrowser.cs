using Microsoft.EntityFrameworkCore;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

public enum WisdomLens
{
    Active,
    Contested,
    Orphaned,
    Retired,
}

public sealed record WisdomQuery(
    Guid ProjectId,
    string? Search = null,
    WisdomKind? Kind = null,
    WisdomLens Lens = WisdomLens.Active);

public sealed record WisdomKindCount(WisdomKind Kind, int Count);

public sealed record WisdomListing(
    IReadOnlyList<WisdomListEntry> Entries,
    int ProjectOwned,
    int Global,
    IReadOnlyList<WisdomKindCount> Kinds);

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

public sealed record ProvenanceEntry(
    Guid Id,
    Guid? EpisodeId,
    Guid? EpisodeProjectId,
    string? EpisodeCwd,
    DateTimeOffset? EpisodeStartedAt,
    Guid? EventId,
    int? EventSeq,
    EventType? EventType,
    DateTimeOffset? EventAt,
    Guid? HarvestedItemId,
    string? HarvestedPath);

public sealed record WisdomRecall(
    IReadOnlyList<LaneCount> Lanes,
    int MarkedUseful,
    int MarkedNoise)
{
    public int Injections => Lanes.Sum(l => l.Entries);

    public int Unmarked => Injections - MarkedUseful - MarkedNoise;
}

public sealed record WisdomDetail(
    WisdomListEntry Entry,
    IReadOnlyList<WisdomVersion> Versions,
    IReadOnlyList<ProvenanceEntry> Provenance,
    WisdomRecall Recall)
{
    public DateTimeOffset? FirstVersionAt => Versions.Count == 0 ? null : Versions[^1].CreatedAt;

    public int CurrentVersion => Versions.Count == 0 ? 0 : Versions[0].Version;
}

// Internal only because it takes the internal MergeGate (CS0051).
internal sealed class WisdomBrowser(
    IDbContextFactory<MimirDbContext> contexts,
    MergeGate gate,
    TimeProvider clock)
{
    public async Task<WisdomListing> ListAsync(WisdomQuery query, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var chipped = await ToEntries(
                db,
                Search(AmbientUniverse.For(db, query.ProjectId, query.Lens), query.Search)
                    .OrderByDescending(w => w.LastConfirmedAt).ThenBy(w => w.Id))
            .ToListAsync(cancellationToken);

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

        var pattern = LikePattern.Contains(term);
        return wisdom.Where(w =>
            w.Tsv!.Matches(EF.Functions.PlainToTsQuery("english", term))
            || EF.Functions.ILike(w.Text, pattern, LikePattern.EscapeCharacter));
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
                    .Select(e => new { e.Seq, e.Type, e.At, e.EpisodeId }).FirstOrDefault(),
                EpisodeId = p.EpisodeId
                    ?? db.Events.Where(e => e.Id == p.EventId).Select(e => (Guid?)e.EpisodeId).FirstOrDefault(),
            })
            .Select(p => new
            {
                p.Id,
                p.HarvestedItemId,
                p.Path,
                p.EventId,
                p.Event,
                p.EpisodeId,
                Episode = db.Episodes
                    .Where(e => e.Id == p.EpisodeId)
                    .Select(e => new { e.ProjectId, e.Cwd, e.StartedAt })
                    .FirstOrDefault(),
            })
            .Select(p => new ProvenanceEntry(
                p.Id,
                p.EpisodeId,
                p.Episode != null ? p.Episode.ProjectId : null,
                p.Episode != null ? p.Episode.Cwd : null,
                p.Episode != null ? p.Episode.StartedAt : null,
                p.EventId,
                p.Event != null ? p.Event.Seq : null,
                p.Event != null ? p.Event.Type : null,
                p.Event != null ? p.Event.At : null,
                p.HarvestedItemId,
                p.Path))
            .ToListAsync(cancellationToken);

        return new WisdomDetail(entry, versions, provenance, await RecallOfAsync(db, wisdomId, cancellationToken));
    }

    private static async Task<WisdomRecall> RecallOfAsync(
        MimirDbContext db, Guid wisdomId, CancellationToken cancellationToken)
    {
        var counted = await db.Injections.AsNoTracking()
            .Where(i => i.Items.Any(x => x.WisdomId == wisdomId))
            .GroupBy(i => new { i.Lane, i.Verdict })
            .Select(g => new { g.Key.Lane, g.Key.Verdict, Entries = g.Count() })
            .ToListAsync(cancellationToken);

        return new WisdomRecall(
            [.. Enum.GetValues<InjectionLane>().Select(lane => new LaneCount(
                lane, counted.Where(c => c.Lane == lane).Sum(c => c.Entries)))],
            counted.Where(c => c.Verdict == InjectionVerdict.Useful).Sum(c => c.Entries),
            counted.Where(c => c.Verdict == InjectionVerdict.Noise).Sum(c => c.Entries));
    }

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

    public async Task EditAsync(Guid wisdomId, string text, CancellationToken cancellationToken)
        => await gate.EditAsync(wisdomId, text, cancellationToken);

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

    public async Task DeleteAsync(Guid wisdomId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Wisdom.Where(w => w.Id == wisdomId).ExecuteDeleteAsync(cancellationToken);
    }
}
