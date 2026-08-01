using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// One sidebar entry (spec §8): a real Project, or the reserved Global pseudo-project, with how
/// much active Wisdom it scopes.
/// </summary>
public sealed record ProjectListItem(Guid Id, string DisplayName, bool IsGlobal, int WisdomCount);

/// <summary>
/// The header's whole-install pipeline readout: Episodes captured, Sealed Episodes still owed
/// distillation, active Wisdom admitted, and Injections recalled today (UTC).
/// </summary>
public sealed record HeaderPipeline(int Episodes, int Queued, int Wisdom, int RecalledToday, bool Distilling);

/// <summary>The tab strip's per-Project counts — a different question from <see cref="HeaderPipeline"/>'s whole-install one.</summary>
public sealed record SurfaceCounts(int Wisdom, int Episodes, int Injections);

/// <summary>
/// The sidebar's "Needs attention" group. Counted over the Project's <see cref="AmbientUniverse"/>
/// rather than its own Scope, unlike the per-Project counts beside each Project: each of these
/// three is the label on a link, and a count that disagreed with the list its own link opens —
/// "Retired 0" opening three Global ones — would be a worse discrepancy than the one ADR-0009
/// accepted. The per-Project counts stay Project-owned because those still have to partition.
/// </summary>
public sealed record WisdomAttention(int Contested, int Orphaned, int Retired);

/// <summary>
/// The sidebar's "Capture" group, scoped to one Project's Episodes. <see cref="QueueDepth"/> is
/// narrower than <see cref="HeaderPipeline.Queued"/> — Sealed and still <c>Pending</c>, Failed
/// broken out separately into <see cref="Failed"/> instead of counted in — so a Sealed Episode
/// stuck Failed shows up here, not in the queue depth, even though the header's Queued still
/// counts it.
/// </summary>
public sealed record CaptureAttention(int Running, int Failed, int QueueDepth);

/// <summary>The sidebar's "Recall" group, scoped to one Project's Injections.</summary>
public sealed record RecallAttention(int MarkedUseful, int MarkedNoise, int WisdomSinceDeleted);

/// <summary>
/// The read-only surface behind the chassis (spec §8): the Project sidebar, the header's
/// whole-install pipeline readout, the tab strip's per-Project counts, and the sidebar's
/// per-surface second group. Every method opens its own short-lived context, like the other UI
/// browsers. Reads Wisdom, Episode and Injection counts but never writes any of them, so unlike
/// <see cref="WisdomBrowser"/> nothing here forces this class internal — keeping it public keeps
/// its Blazor consumers public too.
/// </summary>
public sealed class ChassisBrowser(IDbContextFactory<MimirDbContext> contexts, TimeProvider clock)
{
    public async Task<IReadOnlyList<ProjectListItem>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var ordered = db.Projects.OrderBy(p => p.Id != Project.GlobalId).ThenBy(p => p.DisplayName);
        return await ToProjectItems(db, ordered).ToListAsync(cancellationToken);
    }

    public async Task<ProjectListItem?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await ToProjectItems(db, db.Projects.Where(p => p.Id == projectId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The one projection <see cref="ListProjectsAsync"/> and <see cref="GetProjectAsync"/> share.</summary>
    private static IQueryable<ProjectListItem> ToProjectItems(MimirDbContext db, IQueryable<Project> projects)
        => projects.Select(p => new ProjectListItem(
            p.Id,
            p.DisplayName,
            p.Id == Project.GlobalId,
            db.Wisdom.Count(w => w.ScopeProjectId == p.Id && w.RetiredAt == null)));

    public async Task<HeaderPipeline> GetHeaderPipelineAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var today = clock.GetUtcNow().UtcDateTime.Date;

        var episodes = await db.Episodes.CountAsync(cancellationToken);
        var queued = await db.Episodes.CountAsync(
            e => e.SealedAt != null && e.Distillation != DistillationState.Done, cancellationToken);
        var wisdom = await db.Wisdom.CountAsync(w => w.RetiredAt == null, cancellationToken);
        var recalledToday = await db.Injections.CountAsync(i => i.At >= today, cancellationToken);
        var distilling = await db.Episodes.AnyAsync(e => e.Distillation == DistillationState.Running, cancellationToken);

        return new HeaderPipeline(episodes, queued, wisdom, recalledToday, distilling);
    }

    public async Task<SurfaceCounts> GetSurfaceCountsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var wisdom = await db.Wisdom.CountAsync(
            w => w.ScopeProjectId == projectId && w.RetiredAt == null, cancellationToken);
        var episodes = await db.Episodes.CountAsync(e => e.ProjectId == projectId, cancellationToken);
        var injections = await db.Injections.CountAsync(i => i.ProjectId == projectId, cancellationToken);

        return new SurfaceCounts(wisdom, episodes, injections);
    }

    /// <summary>
    /// Each figure is the length of the list its own sidebar link opens, counted through the one
    /// keeper that produces that list — so "what needs attention" and "what the link shows" cannot
    /// drift into two rules (#91).
    /// </summary>
    public async Task<WisdomAttention> GetWisdomAttentionAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var contested = await AmbientUniverse.For(db, projectId, WisdomLens.Contested)
            .CountAsync(cancellationToken);
        var orphaned = await AmbientUniverse.For(db, projectId, WisdomLens.Orphaned)
            .CountAsync(cancellationToken);
        var retired = await AmbientUniverse.For(db, projectId, WisdomLens.Retired)
            .CountAsync(cancellationToken);

        return new WisdomAttention(contested, orphaned, retired);
    }

    public async Task<CaptureAttention> GetCaptureAttentionAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var running = await db.Episodes.CountAsync(
            e => e.ProjectId == projectId && e.Distillation == DistillationState.Running, cancellationToken);
        var failed = await db.Episodes.CountAsync(
            e => e.ProjectId == projectId && e.Distillation == DistillationState.Failed, cancellationToken);
        var queueDepth = await db.Episodes.CountAsync(
            e => e.ProjectId == projectId && e.SealedAt != null && e.Distillation == DistillationState.Pending,
            cancellationToken);

        return new CaptureAttention(running, failed, queueDepth);
    }

    public async Task<RecallAttention> GetRecallAttentionAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var useful = await db.Injections.CountAsync(
            i => i.ProjectId == projectId && i.Verdict == InjectionVerdict.Useful, cancellationToken);
        var noise = await db.Injections.CountAsync(
            i => i.ProjectId == projectId && i.Verdict == InjectionVerdict.Noise, cancellationToken);

        // A server-side EXISTS/NOT EXISTS over the jsonb, the same shape InjectionBrowser.ListAsync
        // uses for its own reads — no full-table materialization, unlike a prior version of this
        // method that pulled every row (plus its jsonb Items) into memory for one integer.
        var sinceDeleted = await db.Injections
            .Where(i => i.ProjectId == projectId && i.Items.Count > 0
                && i.Items.Any(x => !db.Wisdom.Any(w => w.Id == x.WisdomId)))
            .CountAsync(cancellationToken);

        return new RecallAttention(useful, noise, sinceDeleted);
    }

    /// <summary>
    /// First run is "no non-Global Project exists" (not "no Episodes") — a Project is created by a
    /// session's first hook, so its existence proves Mimir has been introduced, and §8.2 permits
    /// deleting every Episode without the install becoming first-run again.
    /// </summary>
    public async Task<bool> IsFirstRunAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return !await db.Projects.AnyAsync(p => p.Id != Project.GlobalId, cancellationToken);
    }
}
