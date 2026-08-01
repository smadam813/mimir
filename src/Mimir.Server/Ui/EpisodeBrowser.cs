using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

public sealed record EpisodeSummary(
    Guid Id,
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? SealedAt,
    string? SealReason,
    string Cwd,
    int EventCount,
    DistillationState Distillation,
    int WisdomCount)
{
    public EpisodeState State => EpisodeDisplay.State(SealedAt, Distillation);
}

public sealed record EpisodeWisdom(
    Guid Id, WisdomKind Kind, string Text, bool IsGlobal, int Reinforcement);

public sealed record EpisodeDetail(
    Episode Episode, IReadOnlyList<Event> Events, IReadOnlyList<EpisodeWisdom> Produced)
{
    public int PromptCount => Events.Count(e => e.Type == EventType.UserPromptSubmit);
}

public sealed class EpisodeBrowser(IDbContextFactory<MimirDbContext> contexts, IEpisodeFeed feed)
{
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
                db.Provenance
                    .Where(p => p.EpisodeId == e.Id
                        && db.Wisdom.Any(w => w.Id == p.WisdomId && w.RetiredAt == null))
                    .Select(p => p.WisdomId)
                    .Distinct()
                    .Count()))
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

        var produced = await db.Wisdom.AsNoTracking()
            .Where(w => w.RetiredAt == null
                && db.Provenance.Any(p => p.WisdomId == w.Id && p.EpisodeId == episodeId))
            .OrderByDescending(w => w.LastConfirmedAt)
            .ThenBy(w => w.Id)
            .Select(w => new EpisodeWisdom(
                w.Id,
                w.Kind,
                w.Text,
                w.ScopeProjectId == Project.GlobalId,
                w.Reinforcement))
            .ToListAsync(cancellationToken);

        return new EpisodeDetail(episode, events, produced);
    }

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
