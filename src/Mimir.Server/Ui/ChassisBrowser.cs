using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

public sealed record ProjectListItem(Guid Id, string DisplayName, bool IsGlobal, int WisdomCount);

public sealed record HeaderPipeline(int Episodes, int Queued, int Wisdom, int RecalledToday, bool Distilling);

public sealed record SurfaceCounts(int Wisdom, int Episodes, int Injections);

public sealed record WisdomAttention(int Contested, int Orphaned, int Retired);

public sealed record CaptureAttention(int Running, int Failed, int QueueDepth);

public sealed record RecallAttention(int MarkedUseful, int MarkedNoise, int WisdomSinceDeleted);

// Public where WisdomBrowser is internal: nothing here takes an internal type.
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

    private static IQueryable<ProjectListItem> ToProjectItems(MimirDbContext db, IQueryable<Project> projects)
        => projects.Select(p => new ProjectListItem(
            p.Id,
            p.DisplayName,
            p.Id == Project.GlobalId,
            db.Wisdom.Count(w => w.ScopeProjectId == p.Id && w.RetiredAt == null)));

    /// <summary>
    /// The header's live readout, across every Project. <c>Queued</c> restates
    /// <c>DistillationQueue.QueueDepthAsync</c>'s predicate verbatim rather than calling it: that
    /// class is scoped to a request's own <c>MimirDbContext</c>, while every UI browser opens its
    /// own short-lived one from a Singleton.
    /// </summary>
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

        var sinceDeleted = await db.Injections
            .Where(i => i.ProjectId == projectId && i.Items.Count > 0
                && i.Items.Any(x => !db.Wisdom.Any(w => w.Id == x.WisdomId)))
            .CountAsync(cancellationToken);

        return new RecallAttention(useful, noise, sinceDeleted);
    }

    public async Task<bool> IsFirstRunAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return !await db.Projects.AnyAsync(p => p.Id != Project.GlobalId, cancellationToken);
    }
}
