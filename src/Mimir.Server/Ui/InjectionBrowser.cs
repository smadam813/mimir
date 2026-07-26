using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Npgsql;

namespace Mimir.Server.Ui;

/// <summary>
/// One injected Wisdom on a log entry (§8.3): the score that ranked it, whether it took §7's
/// salience boost, and the same row the Wisdom surface renders — curation follows Wisdom wherever
/// it appears (§8). <see cref="Wisdom"/> is null when it was hard-deleted after the injection; the
/// entry still shows that something was injected.
///
/// <see cref="Salient"/> is read now, not as of the injection — nothing records the boost a lane
/// applied — so it explains a score rather than reproducing one.
/// </summary>
public sealed record InjectedWisdom(Guid WisdomId, double Score, bool Salient, WisdomListEntry? Wisdom);

/// <summary>One §8.3 log entry: what a lane injected, its size, mark, and promotion state.</summary>
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
    /// <summary>
    /// §8.3: promotion needs a query to replay (Brief entries carry none, §3) and a live Wisdom
    /// to expect — recall never surfaces a retired or hard-deleted row (§7), so an entry whose
    /// items all went that way has nothing a GoldenCase could ever rank.
    /// </summary>
    public bool CanPromote => QueryContext is not null
        && Items.Any(i => i.Wisdom is { RetiredAt: null });
}

/// <summary>One session's entries, newest first (§8.3).</summary>
public sealed record InjectionSession(string SessionId, IReadOnlyList<InjectionLogEntry> Entries);

/// <summary>One lane's share of the Project's whole log — the list's lane chips (§8.3).</summary>
public sealed record LaneCount(InjectionLane Lane, int Entries);

/// <summary>
/// One Wisdom in the aside's "most recalled this week" (§8.3): how many of the week's entries
/// carried it, and the row to link to. <see cref="Wisdom"/> is null when it was hard-deleted since
/// — it still did the work the count records.
/// </summary>
public sealed record RecalledWisdom(Guid WisdomId, int Recalls, WisdomListEntry? Wisdom);

/// <summary>
/// The §8.3 filters. <see cref="Search"/> narrows on <c>query_context</c>, which is the only text an
/// entry carries (§3) — so it can never match a Brief, which has none.
/// </summary>
public sealed record InjectionQuery(Guid ProjectId, string? Search = null, InjectionLane? Lane = null);

/// <summary>
/// The §8.3 tab: the filtered, bounded listing, plus the Project-wide figures the aside carries.
///
/// The aside's numbers are deliberately the whole Project's, never the filtered listing's:
/// <see cref="Precision"/> is a §9 stat over the whole history, and a figure that moved when a
/// curator typed in the search box would be a different stat under the same name. Only
/// <see cref="Sessions"/> and <see cref="Matching"/> answer the query.
/// </summary>
public sealed record InjectionLogView(
    IReadOnlyList<InjectionSession> Sessions,
    int Matching,
    int Useful,
    int Marked,
    int TotalEntries,
    int TotalSessions,
    int PromotedCases,
    IReadOnlyList<LaneCount> Lanes,
    IReadOnlyList<RecalledWisdom> MostRecalledThisWeek)
{
    /// <summary>§9 injection precision: useful / marked. Null until anything is marked.</summary>
    public double? Precision => Marked == 0 ? null : (double)Useful / Marked;

    /// <summary>The §9 mark's other face — every marked entry is one or the other.</summary>
    public int Noise => Marked - Useful;

    /// <summary>How much of the log a curator has not judged yet, whole history.</summary>
    public int Unmarked => TotalEntries - Marked;

    /// <summary>How many entries the listing actually rendered, after the bound.</summary>
    public int Listed => Sessions.Sum(s => s.Entries.Count);

    /// <summary>
    /// True when entries matching the query fell off the listing at the recent-entry bound.
    /// Measured against <see cref="Matching"/>, not <see cref="TotalEntries"/>: a search that
    /// narrows to three entries has truncated nothing, however long the whole log is.
    /// </summary>
    public bool Truncated => Listed < Matching;
}

/// <summary>
/// The read-and-mark surface behind the injection log (§8.3). Every method opens its own
/// short-lived context, like the other browsers. Marks are the §9 verdicts — they feed the
/// precision stat and golden promotion, nothing else in v1 — and promotion is the one write
/// that grows the golden set from the UI.
/// </summary>
public sealed class InjectionBrowser(IDbContextFactory<MimirDbContext> contexts, TimeProvider clock)
{
    /// <summary>
    /// Bounds the listing: the log accrues one row per non-empty recall decision across every
    /// session — the fastest-growing surface in the schema — so the tab renders only the most
    /// recent entries and says when older ones are cut. The §9 precision inputs deliberately
    /// stay whole-history; a display bound must not move the stat.
    /// </summary>
    internal const int RecentEntryLimit = 100;

    /// <summary>How many Wisdom the aside's "most recalled this week" names.</summary>
    internal const int MostRecalledLimit = 5;

    /// <summary>The window "most recalled this week" looks back over.</summary>
    private static readonly TimeSpan RecalledWindow = TimeSpan.FromDays(7);

    public async Task<InjectionLogView> ListAsync(
        InjectionQuery query, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        // Two queryables on purpose. `scoped` is the whole Project's log and feeds every figure the
        // aside carries — §9's precision above all, which is a whole-history stat and must not move
        // when a curator narrows the list. `matching` is the listing, and the only thing the
        // curator's filters touch.
        var scoped = db.Injections.AsNoTracking().Where(i => i.ProjectId == query.ProjectId);
        var matching = scoped;
        if (query.Lane is { } lane)
        {
            matching = matching.Where(i => i.Lane == lane);
        }

        if (query.Search?.Trim() is { Length: > 0 } term)
        {
            // query_context is the only text an entry carries (§3) — no tsvector over it, so a
            // case-insensitive substring match, with the LIKE metacharacters escaped so a curator
            // typing "%" searches for one.
            var pattern = "%" + term
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_") + "%";
            matching = matching.Where(i =>
                i.QueryContext != null && EF.Functions.ILike(i.QueryContext, pattern, @"\"));
        }

        var rows = await matching
            .OrderByDescending(i => i.At).ThenByDescending(i => i.Id)
            .Take(RecentEntryLimit)
            .ToListAsync(cancellationToken);
        var matchingCount = await matching.CountAsync(cancellationToken);

        var totalEntries = await scoped.CountAsync(cancellationToken);
        var totalSessions = await scoped
            .Select(i => i.SessionId).Distinct().CountAsync(cancellationToken);
        var useful = await scoped
            .CountAsync(i => i.Verdict == InjectionVerdict.Useful, cancellationToken);
        var marked = await scoped.CountAsync(i => i.Verdict != null, cancellationToken);
        var promotedCases = await db.GoldenCases.CountAsync(
            g => g.ProjectId == query.ProjectId && g.CreatedFromInjectionId != null,
            cancellationToken);

        var laneRows = await scoped
            .GroupBy(i => i.Lane)
            .Select(g => new { Lane = g.Key, Entries = g.Count() })
            .ToListAsync(cancellationToken);
        // Every lane, including the ones this Project has never used: a chip that vanishes at zero
        // reads as "this lane does not exist" rather than "this lane injected nothing".
        var lanes = Enum.GetValues<InjectionLane>()
            .Select(l => new LaneCount(
                l, laneRows.FirstOrDefault(r => r.Lane == l)?.Entries ?? 0))
            .ToList();

        // Grouped server-side over the jsonb rather than materialized: this is a leaderboard over a
        // week of the fastest-growing table in the schema, and pulling its Items back for one Take
        // is the mistake ChassisBrowser.GetRecallAttentionAsync already had to undo.
        var since = clock.GetUtcNow() - RecalledWindow;
        var recalled = await scoped
            .Where(i => i.At >= since)
            .SelectMany(i => i.Items)
            .GroupBy(x => x.WisdomId)
            .Select(g => new { WisdomId = g.Key, Recalls = g.Count() })
            .OrderByDescending(r => r.Recalls).ThenBy(r => r.WisdomId)
            .Take(MostRecalledLimit)
            .ToListAsync(cancellationToken);

        var wisdomIds = rows
            .SelectMany(i => i.Items.Select(x => x.WisdomId))
            .Concat(recalled.Select(r => r.WisdomId))
            .Distinct()
            .ToList();
        var wisdom = await WisdomBrowser
            .ToEntries(db, db.Wisdom.Where(w => wisdomIds.Contains(w.Id)))
            .ToDictionaryAsync(w => w.Id, cancellationToken);
        // §7's salience definition, composed rather than restated — the boost the screen explains
        // has to be the one the lanes actually score with.
        var salient = (await ExplicitSalience.Ids(db)
                .Where(id => wisdomIds.Contains(id))
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // The partial unique index on created_from_injection_id caps the breadcrumb at one case
        // per entry, so this lookup cannot collide.
        var entryIds = rows.Select(i => i.Id).ToList();
        var promoted = await db.GoldenCases
            .Where(g => g.CreatedFromInjectionId != null
                && entryIds.Contains(g.CreatedFromInjectionId.Value))
            .ToDictionaryAsync(
                g => g.CreatedFromInjectionId!.Value, g => g.Id, cancellationToken);

        // Rows arrive newest first, so sessions order by their latest entry and entries stay
        // newest-first within each session.
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

        return new InjectionLogView(
            sessions,
            matchingCount,
            useful,
            marked,
            totalEntries,
            totalSessions,
            promotedCases,
            lanes,
            [.. recalled.Select(r => new RecalledWisdom(
                r.WisdomId, r.Recalls, wisdom.GetValueOrDefault(r.WisdomId)))]);
    }

    /// <summary>
    /// The one-click §9 mark, for the entry as a whole. Re-marking switches the verdict and
    /// refreshes <c>verdict_at</c> — the mark reflects the curator's latest word.
    /// </summary>
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

    /// <summary>
    /// §8.3 promote-to-golden: a GoldenCase filled from the entry's <c>query_context</c> and
    /// <c>project_id</c>, expecting the entry's top-ranked still-live Wisdom — neither retired
    /// nor hard-deleted, because recall filters retired rows (§7) and a case expecting one
    /// could never pass. Idempotent — a repeat click returns the existing case. Null when the
    /// entry cannot promote: no <c>query_context</c> (Brief entries), or no injected Wisdom
    /// left alive.
    /// </summary>
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
            // A concurrent click won the insert; the partial unique breadcrumb index makes
            // the idempotency durable, so yield to the case that landed.
            return await db.GoldenCases
                .Where(g => g.CreatedFromInjectionId == injectionId)
                .Select(g => (Guid?)g.Id)
                .FirstAsync(cancellationToken);
        }

        return goldenCase.Id;
    }
}
