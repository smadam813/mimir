using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.2 against a real Postgres: the queries behind the Episode list, and the hard
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
    public async Task TheList_ShowsOnlyTheProjectsEpisodes_NewestFirst()
    {
        var project = await AddProjectAsync("timeline");
        var other = await AddProjectAsync("other");
        // Seeded out of the order asserted, so a dropped ORDER BY cannot pass on insertion order.
        var old = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-2));
        var fresh = await AddEpisodeAsync(project.Id, startedAt: Now);
        await AddEpisodeAsync(other.Id, startedAt: Now);

        var list = await Browser().ListEpisodesAsync(project.Id, null, Token);

        list.Select(e => e.Id).ShouldBe([fresh.Id, old.Id]);
    }

    [Fact]
    public async Task ASummary_CarriesTheSealAndTheEventCount()
    {
        var project = await AddProjectAsync("summary");
        var episode = await AddEpisodeAsync(
            project.Id, startedAt: Now, sealedAt: Now.AddMinutes(5), sealReason: "exit");
        await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse);
        await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse);

        var summary = (await Browser().ListEpisodesAsync(project.Id, null, Token)).Single();

        summary.SealedAt.ShouldNotBeNull();
        summary.SealReason.ShouldBe("exit");
        summary.EventCount.ShouldBe(2);
        summary.SessionId.ShouldBe(episode.SessionId);
        summary.Cwd.ShouldBe(episode.Cwd);
    }

    [Fact]
    public async Task ASummary_CarriesWhereItsDistillationIs()
    {
        var project = await AddProjectAsync("distillation");
        await AddEpisodeAsync(
            project.Id, sealedAt: Now, distillation: DistillationState.Failed);

        var summary = (await Browser().ListEpisodesAsync(project.Id, null, Token)).Single();

        summary.Distillation.ShouldBe(DistillationState.Failed);
    }

    [Fact]
    public async Task ASummary_CountsTheWisdomTheEpisodeProduced()
    {
        var project = await AddProjectAsync("produced");
        var fruitful = await AddEpisodeAsync(project.Id, startedAt: Now);
        var quiet = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));
        var admitted = await AddWisdomAsync(project.Id, "one lesson");
        var confirmed = await AddWisdomAsync(project.Id, "another lesson");
        await AddProvenanceAsync(admitted.Id, episodeId: fruitful.Id);
        await AddProvenanceAsync(confirmed.Id, episodeId: fruitful.Id);
        // Another Episode's Wisdom must not count towards this one.
        var elsewhere = await AddWisdomAsync(project.Id, "a third lesson");
        await AddProvenanceAsync(elsewhere.Id, episodeId: quiet.Id);

        var list = await Browser().ListEpisodesAsync(project.Id, null, Token);

        list.Single(e => e.Id == fruitful.Id).WisdomCount.ShouldBe(2);
        list.Single(e => e.Id == quiet.Id).WisdomCount.ShouldBe(1);
    }

    [Fact]
    public async Task WisdomDrawnFromSeveralOfOneEpisodesEvents_CountsOnce()
    {
        // The gate writes one Provenance row per provenance Event (§6), so the count is over
        // distinct Wisdom — otherwise a Lesson drawn from three Events reads as three Wisdom.
        var project = await AddProjectAsync("distinct");
        var episode = await AddEpisodeAsync(project.Id);
        var first = await AddEventAsync(episode.Id, seq: 1);
        var second = await AddEventAsync(episode.Id, seq: 2);
        var wisdom = await AddWisdomAsync(project.Id, "one lesson, two Events");
        await AddProvenanceAsync(wisdom.Id, episodeId: episode.Id, eventId: first.Id);
        await AddProvenanceAsync(wisdom.Id, episodeId: episode.Id, eventId: second.Id);

        var summary = (await Browser().ListEpisodesAsync(project.Id, null, Token)).Single();

        summary.WisdomCount.ShouldBe(1);
    }

    [Fact]
    public async Task RetiredWisdom_StopsCountingTowardsTheEpisodeThatProducedIt()
    {
        // Every Wisdom figure in the chassis excludes Retired (ChassisBrowser), and §6.4 Retires the
        // loser of a supersede — so a row must not go on crediting a session for a line that has
        // been taken away.
        var project = await AddProjectAsync("retired");
        var episode = await AddEpisodeAsync(project.Id);
        var standing = await AddWisdomAsync(project.Id, "a lesson that stands");
        var retired = await AddWisdomAsync(project.Id, "a lesson taken away", retiredAt: Now);
        await AddProvenanceAsync(standing.Id, episodeId: episode.Id);
        await AddProvenanceAsync(retired.Id, episodeId: episode.Id);

        var summary = (await Browser().ListEpisodesAsync(project.Id, null, Token)).Single();

        summary.WisdomCount.ShouldBe(1);
    }

    [Fact]
    public async Task Searching_KeepsOnlyTheEpisodesWhoseEventsMatch()
    {
        var project = await AddProjectAsync("search");
        var matching = await AddEpisodeAsync(project.Id, startedAt: Now);
        await AddEventAsync(
            matching.Id, seq: 1, payload: """{"prompt":"the interceptor never fires"}""");
        var other = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));
        await AddEventAsync(other.Id, seq: 1, payload: """{"prompt":"bring the stack up"}""");

        var list = await Browser().ListEpisodesAsync(project.Id, "interceptor", Token);

        list.Select(e => e.Id).ShouldBe([matching.Id]);
    }

    [Fact]
    public async Task Searching_IsWordAware_NotSubstring()
    {
        // The GIN index over Event.tsv is an FTS index: it stems, so "fires" finds "firing" — and
        // a mid-word fragment finds nothing, which is the trade the index buys.
        var project = await AddProjectAsync("stemming");
        var episode = await AddEpisodeAsync(project.Id);
        await AddEventAsync(episode.Id, seq: 1, payload: """{"prompt":"the interceptor is firing"}""");

        var stemmed = await Browser().ListEpisodesAsync(project.Id, "fires", Token);
        var fragment = await Browser().ListEpisodesAsync(project.Id, "ercept", Token);

        stemmed.Select(e => e.Id).ShouldBe([episode.Id]);
        fragment.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEpisodeWithNoEvents_IsSearchedAway_ButListedWhenNothingIsTyped()
    {
        var project = await AddProjectAsync("eventless");
        var episode = await AddEpisodeAsync(project.Id);

        (await Browser().ListEpisodesAsync(project.Id, "interceptor", Token)).ShouldBeEmpty();
        (await Browser().ListEpisodesAsync(project.Id, "   ", Token))
            .Select(e => e.Id).ShouldBe([episode.Id]);
    }

    [Fact]
    public async Task Searching_StaysInsideTheProject()
    {
        var project = await AddProjectAsync("scoped");
        var other = await AddProjectAsync("elsewhere");
        var mine = await AddEpisodeAsync(project.Id);
        await AddEventAsync(mine.Id, seq: 1, payload: """{"prompt":"the interceptor never fires"}""");
        var theirs = await AddEpisodeAsync(other.Id);
        await AddEventAsync(theirs.Id, seq: 1, payload: """{"prompt":"the interceptor never fires"}""");

        var list = await Browser().ListEpisodesAsync(project.Id, "interceptor", Token);

        list.Select(e => e.Id).ShouldBe([mine.Id]);
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
    public async Task TheDrillDown_NamesTheWisdomThisEpisodeProduced_NewestConfirmationFirst()
    {
        var project = await AddProjectAsync("produced");
        var episode = await AddEpisodeAsync(project.Id);
        // Seeded out of the order asserted, so a dropped ORDER BY cannot pass on insertion order.
        var older = await AddWisdomAsync(
            project.Id, "prefer ripgrep", lastConfirmedAt: Now.AddDays(-3), kind: WisdomKind.Preference);
        var newer = await AddWisdomAsync(Project.GlobalId, "always run the linter", lastConfirmedAt: Now);
        await AddProvenanceAsync(older.Id, episodeId: episode.Id);
        await AddProvenanceAsync(newer.Id, episodeId: episode.Id);

        var detail = await Browser().GetEpisodeAsync(episode.Id, Token);

        detail.ShouldNotBeNull();
        detail.Produced.Select(w => w.Id).ShouldBe([newer.Id, older.Id]);
        detail.Produced[1].Kind.ShouldBe(WisdomKind.Preference);
        detail.Produced[1].Text.ShouldBe("prefer ripgrep");
        detail.Produced[1].IsGlobal.ShouldBeFalse();
        detail.Produced[0].IsGlobal.ShouldBeTrue();
    }

    [Fact]
    public async Task WisdomDrawnFromSeveralOfOneEpisodesEvents_IsOneLine()
    {
        // The gate writes one Provenance row per provenance Event (§6), so a line drawn from three
        // moments of one session is one thing the session produced, not three.
        var project = await AddProjectAsync("distinct-detail");
        var episode = await AddEpisodeAsync(project.Id);
        var first = await AddEventAsync(episode.Id, seq: 1);
        var second = await AddEventAsync(episode.Id, seq: 2);
        var wisdom = await AddWisdomAsync(project.Id, "one lesson");
        await AddProvenanceAsync(wisdom.Id, episodeId: episode.Id, eventId: first.Id);
        await AddProvenanceAsync(wisdom.Id, episodeId: episode.Id, eventId: second.Id);

        var detail = await Browser().GetEpisodeAsync(episode.Id, Token);

        detail.ShouldNotBeNull();
        detail.Produced.Select(w => w.Id).ShouldBe([wisdom.Id]);
    }

    [Fact]
    public async Task RetiredWisdom_StopsBeingSomethingTheEpisodeProduced()
    {
        // The one convention every Wisdom figure in the chassis keeps, and the drill-down has to
        // agree with the row the curator arrived from.
        var project = await AddProjectAsync("retired-detail");
        var episode = await AddEpisodeAsync(project.Id);
        var standing = await AddWisdomAsync(project.Id, "still true");
        var retired = await AddWisdomAsync(project.Id, "was true", retiredAt: Now);
        await AddProvenanceAsync(standing.Id, episodeId: episode.Id);
        await AddProvenanceAsync(retired.Id, episodeId: episode.Id);

        var detail = await Browser().GetEpisodeAsync(episode.Id, Token);

        detail.ShouldNotBeNull();
        detail.Produced.Select(w => w.Id).ShouldBe([standing.Id]);
    }

    [Fact]
    public async Task AnotherEpisodesWisdom_IsNotThisOnesToClaim()
    {
        var project = await AddProjectAsync("scoped-detail");
        var mine = await AddEpisodeAsync(project.Id);
        var theirs = await AddEpisodeAsync(project.Id);
        var wisdom = await AddWisdomAsync(project.Id, "their lesson");
        await AddProvenanceAsync(wisdom.Id, episodeId: theirs.Id);

        var detail = await Browser().GetEpisodeAsync(mine.Id, Token);

        detail.ShouldNotBeNull();
        detail.Produced.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheDrillDown_CountsPromptsAlone_NotEveryEvent()
    {
        // §3: session start and end are not Events at all, so the turns a curator took are exactly
        // the UserPromptSubmit rows — and a stream is mostly PostToolUse.
        var project = await AddProjectAsync("prompts");
        var episode = await AddEpisodeAsync(project.Id);
        await AddEventAsync(episode.Id, seq: 1, EventType.UserPromptSubmit);
        await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse);
        await AddEventAsync(episode.Id, seq: 3, EventType.Remember, salient: true);
        await AddEventAsync(episode.Id, seq: 4, EventType.UserPromptSubmit);
        await AddEventAsync(episode.Id, seq: 5, EventType.Stop);

        var detail = await Browser().GetEpisodeAsync(episode.Id, Token);

        detail.ShouldNotBeNull();
        detail.Events.Count.ShouldBe(5);
        detail.PromptCount.ShouldBe(2);
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
