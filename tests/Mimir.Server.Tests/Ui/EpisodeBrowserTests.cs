using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.2 against a real Postgres: the queries behind the project sidebar and the Episode
/// timeline, and the hard deletes for sensitive content — an Event alone, or an Episode with
/// everything it holds.
/// </summary>
public sealed class EpisodeBrowserTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly EpisodeFeed _feed = new();

    private readonly List<EpisodeChange> _announced = [];

    public ValueTask InitializeAsync()
    {
        _feed.Subscribe(_announced.Add);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task TheSidebar_ListsGlobalFirst_ThenProjectsByName()
    {
        var beta = await SeedProjectAsync("beta");
        var alpha = await SeedProjectAsync("alpha");

        var projects = await Browser().ListProjectsAsync(Token);

        projects[0].Id.ShouldBe(Project.GlobalId);
        projects[0].IsGlobal.ShouldBeTrue();
        var index = projects.Select(p => p.Id).ToList();
        index.IndexOf(alpha.Id).ShouldBeLessThan(index.IndexOf(beta.Id));
    }

    [Fact]
    public async Task ASingleProject_IsFetchedByItsId_OrNotAtAll()
    {
        var project = await SeedProjectAsync("lookup");

        var found = await Browser().GetProjectAsync(project.Id, Token);
        var missing = await Browser().GetProjectAsync(Guid.NewGuid(), Token);

        found.ShouldNotBeNull();
        found.DisplayName.ShouldBe("lookup");
        found.IsGlobal.ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task TheTimeline_ShowsOnlyTheProjectsEpisodes_NewestFirst()
    {
        var project = await SeedProjectAsync("timeline");
        var other = await SeedProjectAsync("other");
        var old = await SeedEpisodeAsync(project.Id, startedAt: Now.AddHours(-2));
        var fresh = await SeedEpisodeAsync(project.Id, startedAt: Now);
        await SeedEpisodeAsync(other.Id, startedAt: Now);

        var timeline = await Browser().ListEpisodesAsync(project.Id, Token);

        timeline.Select(e => e.Id).ShouldBe([fresh.Id, old.Id]);
    }

    [Fact]
    public async Task ASummary_CarriesTheSealAndTheEventCount()
    {
        var project = await SeedProjectAsync("summary");
        var episode = await SeedEpisodeAsync(project.Id, sealReason: "exit");
        await SeedEventAsync(episode.Id, seq: 1);
        await SeedEventAsync(episode.Id, seq: 2);

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
        var project = await SeedProjectAsync("drill");
        var episode = await SeedEpisodeAsync(project.Id);
        await SeedEventAsync(episode.Id, seq: 2);
        await SeedEventAsync(episode.Id, seq: 1);

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
        var project = await SeedProjectAsync("event-delete");
        var episode = await SeedEpisodeAsync(project.Id);
        var sensitive = await SeedEventAsync(episode.Id, seq: 1);
        var kept = await SeedEventAsync(episode.Id, seq: 2);

        await Browser().DeleteEventAsync(sensitive.Id, Token);

        var remaining = await FromDb(db => db.Events.Where(e => e.EpisodeId == episode.Id).ToListAsync(Token));
        remaining.Select(e => e.Id).ShouldBe([kept.Id]);
        _announced.ShouldBe([new EpisodeChange(project.Id, episode.Id)]);
    }

    [Fact]
    public async Task DeletingAnEpisode_TakesItsEventsWithIt_AndAnnouncesTheChange()
    {
        var project = await SeedProjectAsync("episode-delete");
        var doomed = await SeedEpisodeAsync(project.Id);
        await SeedEventAsync(doomed.Id, seq: 1);
        var kept = await SeedEpisodeAsync(project.Id);
        var keptEvent = await SeedEventAsync(kept.Id, seq: 1);

        await Browser().DeleteEpisodeAsync(doomed.Id, Token);

        (await FromDb(db => db.Episodes.CountAsync(e => e.Id == doomed.Id, Token))).ShouldBe(0);
        // Scoped to this test's two Episodes: the database is shared by the whole class, and
        // sibling tests' Events are present in whatever order the runner chose.
        var events = await FromDb(db => db.Events
            .Where(e => e.EpisodeId == doomed.Id || e.EpisodeId == kept.Id)
            .Select(e => e.Id)
            .ToListAsync(Token));
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

    private EpisodeBrowser Browser() => new(new FixtureContextFactory(fixture), _feed);

    private async Task<Project> SeedProjectAsync(string name)
    {
        // Unique identity per call: the database is shared by the whole test class.
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = $"github.com/test/{name}-{Guid.NewGuid():N}",
            RootPaths = [$@"C:\git\{name}"],
            DisplayName = name,
        };
        await IntoDb(db => db.Projects.Add(project));
        return project;
    }

    private async Task<Episode> SeedEpisodeAsync(
        Guid projectId, DateTimeOffset? startedAt = null, string? sealReason = null)
    {
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{Guid.NewGuid():N}",
            ProjectId = projectId,
            StartedAt = startedAt ?? Now,
            SealedAt = sealReason is null ? null : (startedAt ?? Now).AddMinutes(5),
            SealReason = sealReason,
            Cwd = @"C:\git\browser-tests",
        };
        await IntoDb(db => db.Episodes.Add(episode));
        return episode;
    }

    private async Task<Event> SeedEventAsync(Guid episodeId, int seq)
    {
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episodeId,
            Seq = seq,
            Type = EventType.PostToolUse,
            At = Now,
            Payload = """{"tool_name":"Bash"}""",
            PayloadFullSize = 20,
        };
        await IntoDb(db => db.Events.Add(evt));
        return evt;
    }

    private async Task IntoDb(Action<MimirDbContext> mutate)
    {
        await using var db = CreateContext();
        mutate(db);
        await db.SaveChangesAsync(Token);
    }

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var db = CreateContext();
        return await query(db);
    }

    private MimirDbContext CreateContext()
    {
        if (fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(TestPostgres.SkipMessage(reason));
        }

        return fixture.CreateContext();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixtureContextFactory(CaptureDatabaseFixture fixture) : IDbContextFactory<MimirDbContext>
    {
        public MimirDbContext CreateDbContext()
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip(TestPostgres.SkipMessage(reason));
            }

            return fixture.CreateContext();
        }
    }
}
