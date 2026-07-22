using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

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
    /// <summary>§8.3: promotion needs a query to replay, and Brief entries carry none (§3).</summary>
    public bool CanPromote => QueryContext is not null;
}

/// <summary>One session's entries, newest first (§8.3).</summary>
public sealed record InjectionSession(string SessionId, IReadOnlyList<InjectionLogEntry> Entries);

/// <summary>The §8.3 tab: per-session entries plus the §9 injection-precision inputs.</summary>
public sealed record InjectionLogView(IReadOnlyList<InjectionSession> Sessions, int Useful, int Marked)
{
    /// <summary>§9 injection precision: useful / marked. Null until anything is marked.</summary>
    public double? Precision => Marked == 0 ? null : (double)Useful / Marked;
}

/// <summary>
/// The read-and-mark surface behind the injection log (§8.3). Every method opens its own
/// short-lived context, like the other browsers. Marks are the §9 verdicts — they feed the
/// precision stat and golden promotion, nothing else in v1 — and promotion is the one write
/// that grows the golden set from the UI.
/// </summary>
public sealed class InjectionBrowser(IDbContextFactory<MimirDbContext> contexts, TimeProvider clock)
{
    public async Task<InjectionLogView> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Injections.AsNoTracking()
            .Where(i => i.ProjectId == projectId)
            .OrderByDescending(i => i.At).ThenByDescending(i => i.Id)
            .ToListAsync(cancellationToken);

        var wisdomIds = rows.SelectMany(i => i.Items.Select(x => x.WisdomId)).Distinct().ToList();
        var wisdom = await WisdomBrowser
            .ToEntries(db, db.Wisdom.Where(w => wisdomIds.Contains(w.Id)))
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        // Promotion is idempotent, but hand-inserted cases (§9) may carry the same breadcrumb —
        // any case for the entry counts as "promoted", so duplicates collapse instead of throwing.
        var entryIds = rows.Select(i => i.Id).ToList();
        var promoted = (await db.GoldenCases
                .Where(g => g.CreatedFromInjectionId != null
                    && entryIds.Contains(g.CreatedFromInjectionId.Value))
                .Select(g => new { InjectionId = g.CreatedFromInjectionId!.Value, g.Id })
                .ToListAsync(cancellationToken))
            .GroupBy(g => g.InjectionId)
            .ToDictionary(g => g.Key, g => g.First().Id);

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

        return new InjectionLogView(
            sessions,
            Useful: rows.Count(i => i.Verdict == InjectionVerdict.Useful),
            Marked: rows.Count(i => i.Verdict != null));
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
    /// <c>project_id</c>, expecting the entry's top-ranked still-existing Wisdom. Idempotent —
    /// a repeat click returns the existing case. Null when the entry cannot promote: no
    /// <c>query_context</c> (Brief entries), or every injected Wisdom hard-deleted since.
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
            .Where(w => rankedIds.Contains(w.Id))
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
        await db.SaveChangesAsync(cancellationToken);
        return goldenCase.Id;
    }
}
