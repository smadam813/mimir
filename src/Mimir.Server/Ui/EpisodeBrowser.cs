using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// One row of the Episode list (spec §8.2). Unsealed means the session is live (or crashed, §4).
/// <paramref name="WisdomCount"/> is how much durable memory this session is Provenance for that
/// still stands — Wisdom admitted from it and Wisdom it confirmed, since the Merge Gate unions
/// provenance onto the line it reinforces (§6.3) and both readings are "what this session
/// produced". Retired Wisdom is excluded, the one convention every Wisdom figure in the chassis
/// keeps (<see cref="ChassisBrowser"/>): a curator who Retires a bad line expects the row that
/// produced it to stop claiming it.
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
    int WisdomCount)
{
    /// <summary>
    /// What the row marks. Derived here so the list's four readers — the chips' counts, the chip
    /// filter, the row's own mark and <see cref="EpisodeDisplay.MetaLine"/> — ask one question
    /// instead of passing the same two columns around; the rule itself stays
    /// <see cref="EpisodeDisplay.State"/>'s.
    /// </summary>
    public EpisodeState State => EpisodeDisplay.State(SealedAt, Distillation);
}

/// <summary>
/// One line of the drill-down's "What it produced" (#95): a Wisdom this Episode is Provenance for
/// that still stands. Carries what the §8.1 surface's own rows carry, so a curator crossing from
/// the raw tier to the durable one recognises the line when they arrive.
/// </summary>
public sealed record EpisodeWisdom(
    Guid Id, WisdomKind Kind, string Text, bool IsGlobal, int Reinforcement);

/// <summary>
/// The §8.2 drill-down: the Episode, its full Event stream in arrival order, and the durable memory
/// that stream is Provenance for. <paramref name="Produced"/> counts the same way the list row's
/// <see cref="EpisodeSummary.WisdomCount"/> does — one line per Wisdom however many of this
/// Episode's Events it was drawn from, Retired excluded — so the drill-down and the row a curator
/// arrived from never disagree.
/// </summary>
public sealed record EpisodeDetail(
    Episode Episode, IReadOnlyList<Event> Events, IReadOnlyList<EpisodeWisdom> Produced)
{
    /// <summary>
    /// How many turns the curator took, for the aside. Counted over the stream already in hand
    /// rather than asked of Postgres a second time — and §3 makes it exactly the
    /// <see cref="EventType.UserPromptSubmit"/> Events, since session start and end are not Events
    /// at all.
    /// </summary>
    public int PromptCount => Events.Count(e => e.Type == EventType.UserPromptSubmit);
}

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
                // Retired excluded, as every Wisdom figure in the chassis excludes it — and §6.4
                // Retires the loser of a supersede, so a line adjudicated away stops being credited
                // here while the successor, carrying this Episode's provenance, takes its place.
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

        // Existence over a join, so a Wisdom the gate drew from three of this Episode's Events is
        // one line rather than three — the same distinctness ListEpisodesAsync's count takes, by a
        // different route. Retired excluded for the reason stated there. Newest confirmation first:
        // the gate moves LastConfirmedAt on every reinforcement, so the head of this list is the
        // memory this session produced that is still being confirmed by later ones.
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
