using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.2 against a real Postgres: the queries behind the Episode timeline, and the hard
/// deletes for sensitive content — an Event alone, or an Episode with everything it holds. The
/// Project sidebar's own queries moved to <c>ChassisBrowserTests</c> with the methods (#89).
/// </summary>
public sealed class EpisodeBrowserTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private readonly EpisodeFeed _feed = new();

    private readonly List<EpisodeChange> _announced = [];

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _feed.Subscribe(_announced.Add);
    }

    [Fact]
    public async Task TheTimeline_ShowsOnlyTheProjectsEpisodes_NewestFirst()
    {
        var project = await AddProjectAsync("timeline");
        var other = await AddProjectAsync("other");
        var old = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-2));
        var fresh = await AddEpisodeAsync(project.Id, startedAt: Now);
        await AddEpisodeAsync(other.Id, startedAt: Now);

        var timeline = await Browser().ListEpisodesAsync(project.Id, Token);

        timeline.Select(e => e.Id).ShouldBe([fresh.Id, old.Id]);
    }

    [Fact]
    public async Task ASummary_CarriesTheSealAndTheEventCount()
    {
        var project = await AddProjectAsync("summary");
        var episode = await AddEpisodeAsync(
            project.Id, startedAt: Now, sealedAt: Now.AddMinutes(5), sealReason: "exit");
        await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse);
        await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse);

        var summary = (await Browser().ListEpisodesAsync(project.Id, Token)).Single();

        summary.SealedAt.ShouldNotBeNull();
        summary.SealReason.ShouldBe("exit");
        summary.EventCount.ShouldBe(2);
        summary.SessionId.ShouldBe(episode.SessionId);
        summary.Cwd.ShouldBe(episode.Cwd);
    }

    [Fact]
    public async Task TheDrillDown_StreamsEventsInSequence()
    {
        var project = await AddProjectAsync("drill");
        var episode = await AddEpisodeAsync(project.Id);
        await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse);
        await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse);

        var detail = await Browser().GetEpisodeAsync(episode.Id, Token);

        detail.ShouldNotBeNull();
        detail.Episode.Id.ShouldBe(episode.Id);
        detail.Events.Select(e => e.Seq).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task DrillingIntoADeletedEpisode_AnswersNothing()
    {
        (await Browser().GetEpisodeAsync(Guid.NewGuid(), Token)).ShouldBeNull();
    }

    [Fact]
    public async Task DeletingAnEvent_RemovesItAlone_AndAnnouncesTheChange()
    {
        var project = await AddProjectAsync("event-delete");
        var episode = await AddEpisodeAsync(project.Id);
        var sensitive = await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse);
        var kept = await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse);

        await Browser().DeleteEventAsync(sensitive.Id, Token);

        var remaining = await FromDb(db => db.Events.ToListAsync(Token));
        remaining.Select(e => e.Id).ShouldBe([kept.Id]);
        _announced.ShouldBe([new EpisodeChange(project.Id, episode.Id)]);
    }

    [Fact]
    public async Task DeletingAnEpisode_TakesItsEventsWithIt_AndAnnouncesTheChange()
    {
        var project = await AddProjectAsync("episode-delete");
        var doomed = await AddEpisodeAsync(project.Id);
        await AddEventAsync(doomed.Id, seq: 1, EventType.PostToolUse);
        var kept = await AddEpisodeAsync(project.Id);
        var keptEvent = await AddEventAsync(kept.Id, seq: 1, EventType.PostToolUse);

        await Browser().DeleteEpisodeAsync(doomed.Id, Token);

        (await FromDb(db => db.Episodes.CountAsync(e => e.Id == doomed.Id, Token))).ShouldBe(0);
        var events = await FromDb(db => db.Events.Select(e => e.Id).ToListAsync(Token));
        events.ShouldBe([keptEvent.Id]);
        _announced.ShouldBe([new EpisodeChange(project.Id, doomed.Id)]);
    }

    [Fact]
    public async Task DeletingWhatIsAlreadyGone_StaysQuiet()
    {
        await Browser().DeleteEventAsync(Guid.NewGuid(), Token);
        await Browser().DeleteEpisodeAsync(Guid.NewGuid(), Token);

        _announced.ShouldBeEmpty();
    }

    private EpisodeBrowser Browser() => new(Contexts, _feed);
}
