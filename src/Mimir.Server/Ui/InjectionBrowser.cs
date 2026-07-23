using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Npgsql;

namespace Mimir.Server.Ui;

/// <summary>
/// One injected Wisdom on a log entry (§8.3): the score that ranked it, and the same card entry
/// the browser renders — curation follows Wisdom wherever it appears (§8). Null when the Wisdom
/// was hard-deleted after the injection; the entry still shows that something was injected.
/// </summary>
public sealed record InjectedWisdom(Guid WisdomId, double Score, WisdomListEntry? Wisdom);

/// <summary>One §8.3 log entry: what a lane injected, its size, mark, and promotion state.</summary>
public sealed record InjectionLogEntry(
    Guid Id,
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

/// <summary>The §8.3 tab: per-session entries plus the §9 injection-precision inputs.</summary>
public sealed record InjectionLogView(
    IReadOnlyList<InjectionSession> Sessions, int Useful, int Marked, int TotalEntries)
{
    /// <summary>§9 injection precision: useful / marked. Null until anything is marked.</summary>
    public double? Precision => Marked == 0 ? null : (double)Useful / Marked;

    /// <summary>True when entries older than the recent-entry bound fell off the listing.</summary>
    public bool Truncated => Sessions.Sum(s => s.Entries.Count) < TotalEntries;
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

    public async Task<InjectionLogView> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var scoped = db.Injections.AsNoTracking().Where(i => i.ProjectId == projectId);
        var rows = await scoped
            .OrderByDescending(i => i.At).ThenByDescending(i => i.Id)
            .Take(RecentEntryLimit)
            .ToListAsync(cancellationToken);
        var totalEntries = await scoped.CountAsync(cancellationToken);
        var useful = await scoped
            .CountAsync(i => i.Verdict == InjectionVerdict.Useful, cancellationToken);
        var marked = await scoped.CountAsync(i => i.Verdict != null, cancellationToken);

        var wisdomIds = rows.SelectMany(i => i.Items.Select(x => x.WisdomId)).Distinct().ToList();
        var wisdom = await WisdomBrowser
            .ToEntries(db, db.Wisdom.Where(w => wisdomIds.Contains(w.Id)))
            .ToDictionaryAsync(w => w.Id, cancellationToken);

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
                        i.At,
                        i.Lane,
                        i.QueryContext,
                        i.Chars,
                        i.Verdict,
                        i.VerdictAt,
                        promoted.TryGetValue(i.Id, out var caseId) ? caseId : null,
                        i.Items
                            .Select(x => new InjectedWisdom(
                                x.WisdomId, x.Score, wisdom.GetValueOrDefault(x.WisdomId)))
                            .ToList()))
                    .ToList()))
            .ToList();

        return new InjectionLogView(sessions, useful, marked, totalEntries);
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
