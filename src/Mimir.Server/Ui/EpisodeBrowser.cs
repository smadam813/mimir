using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>One sidebar entry (spec §8): a real Project, or the reserved Global pseudo-project.</summary>
public sealed record ProjectListItem(Guid Id, string DisplayName, bool IsGlobal);

/// <summary>One timeline row (spec §8.2). Unsealed means the session is live (or crashed, §4).</summary>
public sealed record EpisodeSummary(
    Guid Id,
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? SealedAt,
    string? SealReason,
    string Cwd,
    int EventCount);

/// <summary>The §8.2 drill-down: the Episode and its full Event stream in arrival order.</summary>
public sealed record EpisodeDetail(Episode Episode, IReadOnlyList<Event> Events);

/// <summary>
/// The read-and-delete surface behind the project sidebar and the Episode timeline (spec §8.2).
/// Every method opens its own short-lived context — a Blazor circuit outlives any sensible
/// DbContext lifetime. The hard deletes exist for sensitive content and are announced on the
/// feed so every open timeline drops the deleted rows without a refresh.
/// </summary>
public sealed class EpisodeBrowser(IDbContextFactory<MimirDbContext> contexts, IEpisodeFeed feed)
{
    public async Task<IReadOnlyList<ProjectListItem>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var projects = await db.Projects
            .OrderBy(p => p.Id != Project.GlobalId)
            .ThenBy(p => p.DisplayName)
            .Select(p => new ProjectListItem(p.Id, p.DisplayName, p.Id == Project.GlobalId))
            .ToListAsync(cancellationToken);
        return projects;
    }

    public async Task<ProjectListItem?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new ProjectListItem(p.Id, p.DisplayName, p.Id == Project.GlobalId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EpisodeSummary>> ListEpisodesAsync(
        Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Episodes
            .Where(e => e.ProjectId == projectId)
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new EpisodeSummary(
                e.Id,
                e.SessionId,
                e.StartedAt,
                e.SealedAt,
                e.SealReason,
                e.Cwd,
                db.Events.Count(v => v.EpisodeId == e.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<EpisodeDetail?> GetEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var episode = await db.Episodes.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == episodeId, cancellationToken);
        if (episode is null)
        {
            return null;
        }

        var events = await db.Events.AsNoTracking()
            .Where(e => e.EpisodeId == episodeId)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken);
        return new EpisodeDetail(episode, events);
    }

    /// <summary>Hard delete of a single Event (§8.2) — the tool for one sensitive payload.</summary>
    public async Task DeleteEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var doomed = await db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new
            {
                e.Id,
                e.EpisodeId,
                ProjectId = db.Episodes.Where(p => p.Id == e.EpisodeId).Select(p => p.ProjectId).First(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (doomed is null)
        {
            return;
        }

        await db.Events.Where(e => e.Id == eventId).ExecuteDeleteAsync(cancellationToken);
        feed.Publish(new EpisodeChange(doomed.ProjectId, doomed.EpisodeId));
    }

    /// <summary>Hard delete of an Episode with every Event it holds (§8.2; the FK cascades).</summary>
    public async Task DeleteEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var doomed = await db.Episodes
            .Where(e => e.Id == episodeId)
            .Select(e => new { e.Id, e.ProjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (doomed is null)
        {
            return;
        }

        await db.Episodes.Where(e => e.Id == episodeId).ExecuteDeleteAsync(cancellationToken);
        feed.Publish(new EpisodeChange(doomed.ProjectId, episodeId));
    }
}
