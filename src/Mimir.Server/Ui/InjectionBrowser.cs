using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Npgsql;

namespace Mimir.Server.Ui;

public sealed record InjectedWisdom(Guid WisdomId, double Score, bool Salient, WisdomListEntry? Wisdom);

public sealed record InjectionLogEntry(
    Guid Id,
    string SessionId,
    DateTimeOffset At,
    InjectionLane Lane,
    string? QueryContext,
    int Chars,
    InjectionVerdict? Verdict,
    DateTimeOffset? VerdictAt,
    Guid? PromotedCaseId,
    IReadOnlyList<InjectedWisdom> Items)
{
    public bool CanPromote => QueryContext is not null
        && Items.Any(i => i.Wisdom is { RetiredAt: null });

    public int WisdomSinceDeleted => Items.Count(i => i.Wisdom is null);
}

public sealed record InjectionSession(string SessionId, IReadOnlyList<InjectionLogEntry> Entries);

public sealed record LaneCount(InjectionLane Lane, int Entries);

public sealed record RecalledWisdom(Guid WisdomId, int Recalls, WisdomListEntry? Wisdom);

public sealed record InjectionQuery(Guid ProjectId, string? Search = null, InjectionLane? Lane = null);

public sealed record InjectionListing(IReadOnlyList<InjectionSession> Sessions, int Matching)
{
    public int Listed => Sessions.Sum(s => s.Entries.Count);

    public bool Truncated => Listed < Matching;
}

public sealed record InjectionAside(
    int Useful,
    int Marked,
    int TotalEntries,
    int TotalSessions,
    int PromotedCases,
    IReadOnlyList<LaneCount> Lanes,
    IReadOnlyList<RecalledWisdom> MostRecalledThisWeek)
{
    public double? Precision => Marked == 0 ? null : (double)Useful / Marked;

    public int Noise => Marked - Useful;

    public int Unmarked => TotalEntries - Marked;
}

public sealed class InjectionBrowser(IDbContextFactory<MimirDbContext> contexts, TimeProvider clock)
{
    internal const int RecentEntryLimit = 100;

    internal const int MostRecalledLimit = 5;

    private static readonly TimeSpan RecalledWindow = TimeSpan.FromDays(7);

    public async Task<InjectionListing> ListAsync(
        InjectionQuery query, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var matching = db.Injections.AsNoTracking().Where(i => i.ProjectId == query.ProjectId);
        if (query.Lane is { } lane)
        {
            matching = matching.Where(i => i.Lane == lane);
        }

        if (query.Search?.Trim() is { Length: > 0 } term)
        {
            var pattern = LikePattern.Contains(term);
            matching = matching.Where(i =>
                i.QueryContext != null
                && EF.Functions.ILike(i.QueryContext, pattern, LikePattern.EscapeCharacter));
        }

        var rows = await matching
            .OrderByDescending(i => i.At).ThenByDescending(i => i.Id)
            .Take(RecentEntryLimit)
            .ToListAsync(cancellationToken);

        var matchingCount = rows.Count < RecentEntryLimit
            ? rows.Count
            : await matching.CountAsync(cancellationToken);

        var wisdomIds = rows
            .SelectMany(i => i.Items.Select(x => x.WisdomId))
            .Distinct()
            .ToList();
        var wisdom = await HydrateAsync(db, wisdomIds, cancellationToken);
        var salient = (await ExplicitSalience.Ids(db)
                .Where(id => wisdomIds.Contains(id))
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var entryIds = rows.Select(i => i.Id).ToList();
        var promoted = await db.GoldenCases
            .Where(g => g.CreatedFromInjectionId != null
                && entryIds.Contains(g.CreatedFromInjectionId.Value))
            .ToDictionaryAsync(
                g => g.CreatedFromInjectionId!.Value, g => g.Id, cancellationToken);

        var sessions = rows
            .GroupBy(i => i.SessionId)
            .Select(g => new InjectionSession(
                g.Key,
                g.Select(i => new InjectionLogEntry(
                        i.Id,
                        i.SessionId,
                        i.At,
                        i.Lane,
                        i.QueryContext,
                        i.Chars,
                        i.Verdict,
                        i.VerdictAt,
                        promoted.TryGetValue(i.Id, out var caseId) ? caseId : null,
                        i.Items
                            .Select(x => new InjectedWisdom(
                                x.WisdomId,
                                x.Score,
                                salient.Contains(x.WisdomId),
                                wisdom.GetValueOrDefault(x.WisdomId)))
                            .ToList()))
                    .ToList()))
            .ToList();

        return new InjectionListing(sessions, matchingCount);
    }

    public async Task<InjectionAside> GetAsideAsync(
        Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var scoped = db.Injections.AsNoTracking().Where(i => i.ProjectId == projectId);

        (int TotalEntries, int Useful, int Marked) counts = await scoped
            .GroupBy(_ => 1)
            .Select(g => new ValueTuple<int, int, int>(
                g.Count(),
                g.Count(i => i.Verdict == InjectionVerdict.Useful),
                g.Count(i => i.Verdict != null)))
            .FirstOrDefaultAsync(cancellationToken);

        // Postgres has no partial aggregate for `count(DISTINCT x)`, so folding this one into the
        // group above would cost that query its parallel plan.
        var totalSessions = await scoped
            .Select(i => i.SessionId).Distinct().CountAsync(cancellationToken);

        var promotedCases = await db.GoldenCases.CountAsync(
            g => g.ProjectId == projectId && g.CreatedFromInjectionId != null,
            cancellationToken);

        var laneRows = await scoped
            .GroupBy(i => i.Lane)
            .Select(g => new { Lane = g.Key, Entries = g.Count() })
            .ToListAsync(cancellationToken);
        var lanes = Enum.GetValues<InjectionLane>()
            .Select(l => new LaneCount(
                l, laneRows.FirstOrDefault(r => r.Lane == l)?.Entries ?? 0))
            .ToList();

        var since = clock.GetUtcNow() - RecalledWindow;
        var recalled = await scoped
            .Where(i => i.At >= since)
            .SelectMany(i => i.Items)
            .GroupBy(x => x.WisdomId)
            .Select(g => new { WisdomId = g.Key, Recalls = g.Count() })
            .OrderByDescending(r => r.Recalls).ThenBy(r => r.WisdomId)
            .Take(MostRecalledLimit)
            .ToListAsync(cancellationToken);

        var wisdom = await HydrateAsync(
            db, [.. recalled.Select(r => r.WisdomId)], cancellationToken);

        return new InjectionAside(
            counts.Useful,
            counts.Marked,
            counts.TotalEntries,
            totalSessions,
            promotedCases,
            lanes,
            [.. recalled.Select(r => new RecalledWisdom(
                r.WisdomId, r.Recalls, wisdom.GetValueOrDefault(r.WisdomId)))]);
    }

    private static async Task<Dictionary<Guid, WisdomListEntry>> HydrateAsync(
        MimirDbContext db, List<Guid> wisdomIds, CancellationToken cancellationToken)
        => await WisdomBrowser
            .ToEntries(db, db.Wisdom.Where(w => wisdomIds.Contains(w.Id)))
            .ToDictionaryAsync(w => w.Id, cancellationToken);

    public async Task MarkAsync(
        Guid injectionId, InjectionVerdict verdict, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Injections.Where(i => i.Id == injectionId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(i => i.Verdict, verdict)
                    .SetProperty(i => i.VerdictAt, clock.GetUtcNow()),
                cancellationToken);
    }

    public async Task<Guid?> PromoteAsync(Guid injectionId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var injection = await db.Injections.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == injectionId, cancellationToken);
        if (injection?.QueryContext is null)
        {
            return null;
        }

        var existing = await db.GoldenCases
            .Where(g => g.CreatedFromInjectionId == injectionId)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var rankedIds = injection.Items
            .OrderByDescending(x => x.Score)
            .Select(x => x.WisdomId)
            .ToList();
        var surviving = await db.Wisdom
            .Where(w => rankedIds.Contains(w.Id) && w.RetiredAt == null)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        var expected = rankedIds.FirstOrDefault(surviving.Contains);
        if (expected == Guid.Empty)
        {
            return null;
        }

        var goldenCase = new GoldenCase
        {
            Id = Guid.CreateVersion7(),
            QueryContext = injection.QueryContext,
            ProjectId = injection.ProjectId,
            ExpectedWisdomId = expected,
            CreatedFromInjectionId = injectionId,
            Note = "Promoted from a "
                + injection.Lane
                + " injection of "
                + injection.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        db.GoldenCases.Add(goldenCase);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent click won the insert; unforceable in a test, so nothing pins it.
            return await db.GoldenCases
                .Where(g => g.CreatedFromInjectionId == injectionId)
                .Select(g => (Guid?)g.Id)
                .FirstAsync(cancellationToken);
        }

        return goldenCase.Id;
    }
}
