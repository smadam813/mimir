using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// One row of the Episode list (spec §8.2). Unsealed means the session is live (or crashed, §4).
/// <paramref name="WisdomCount"/> is how much durable memory this session is Provenance for —
/// Wisdom admitted from it and Wisdom it confirmed, since the Merge Gate unions provenance onto
/// the line it reinforces (§6.3) and both readings are "what this session produced".
/// </summary>
public sealed record EpisodeSummary(
    Guid Id,
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? SealedAt,
    string? SealReason,
    string Cwd,
    int EventCount,
    DistillationState Distillation,
    int WisdomCount);

/// <summary>The §8.2 drill-down: the Episode and its full Event stream in arrival order.</summary>
public sealed record EpisodeDetail(Episode Episode, IReadOnlyList<Event> Events);

/// <summary>
/// The read-and-delete surface behind the Episode list (spec §8.2). Every method opens its
/// own short-lived context — a Blazor circuit outlives any sensible DbContext lifetime. The hard
/// deletes exist for sensitive content and are announced on the feed so every open list drops
/// the deleted rows without a refresh. The Project sidebar and lookup moved to
/// <see cref="ChassisBrowser"/> — the sidebar and the Project page are their only callers.
/// </summary>
public sealed class EpisodeBrowser(IDbContextFactory<MimirDbContext> contexts, IEpisodeFeed feed)
{
    /// <param name="search">
    /// Narrows the list to Episodes whose Event stream matches, word-aware over the payload's
    /// string values — the GIN index over <c>Event.tsv</c> the §7 Episode search leg already reads
    /// (<see cref="EventSearch"/>). Null or blank lists every Episode. The Episode's own metadata is
    /// deliberately not searched: a curator scanning for a cwd or a session id has the list itself,
    /// where both are on every row.
    /// </param>
    public async Task<IReadOnlyList<EpisodeSummary>> ListEpisodesAsync(
        Guid projectId, string? search, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var episodes = db.Episodes.Where(e => e.ProjectId == projectId);
        if (search?.Trim() is { Length: > 0 } term)
        {
            episodes = episodes.Where(e => db.Events.Any(v =>
                v.EpisodeId == e.Id && v.Tsv!.Matches(EF.Functions.PlainToTsQuery("english", term))));
        }

        return await episodes
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new EpisodeSummary(
                e.Id,
                e.SessionId,
                e.StartedAt,
                e.SealedAt,
                e.SealReason,
                e.Cwd,
                db.Events.Count(v => v.EpisodeId == e.Id),
                e.Distillation,
                // Distinct: the gate writes one Provenance row per provenance Event (§6), so a
                // Wisdom drawn from three Events of this Episode is one Wisdom produced, not three.
                db.Provenance.Where(p => p.EpisodeId == e.Id).Select(p => p.WisdomId).Distinct().Count()))
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
