using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Contracts.Mcp;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// <c>mimir_remember</c> (§4, §7.1) against a real Postgres: the save lands salient on the most
/// recently active unsealed Episode of the Project — activity, not start order — and with no
/// unsealed Episode the content goes straight through the Merge Gate as a candidate. A deliberate
/// save is never dropped.
/// </summary>
public sealed class McpRememberServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmbeddings _embeddings = new();

    private readonly FakeArbiter _arbiter = new();

    private MimirDbContext? _context;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task LandsSalient_OnTheMostRecentlyActiveUnsealedEpisode()
    {
        var project = await AddProjectAsync();
        // Started earlier but active later — activity, not start order, picks the target (§7.1).
        var activeLater = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-3));
        await AddEventAsync(activeLater, seq: 1, at: Now.AddMinutes(-5));
        var startedLater = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));
        await AddEpisodeAsync(project.Id, startedAt: Now.AddMinutes(-1), sealedAt: Now);

        var text = await RememberAsync(project, "Always run the linter before pushing.", "Lesson");

        text.ShouldContain(activeLater.SessionId);
        text.ShouldContain("salient");
        var saved = await FromDb(db => db.Events
            .SingleAsync(e => e.EpisodeId == activeLater.Id && e.Type == EventType.Remember, Token));
        saved.Salient.ShouldBeTrue();
        saved.Seq.ShouldBe(2);
        saved.Payload.ShouldContain("Always run the linter before pushing.");
        saved.Payload.ShouldContain("Lesson");
        (await FromDb(db => db.Events.CountAsync(e => e.EpisodeId == startedLater.Id, Token)))
            .ShouldBe(0, "the younger-but-idle Episode is not the most recently active");
    }

    [Fact]
    public async Task WithNoUnsealedEpisode_TheContentGoesThroughTheMergeGate()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-2), sealedAt: Now.AddHours(-1));
        const string content = "Prefer trunk-based development on this repo.";

        var text = await RememberAsync(project, content, "preference");

        text.ShouldContain("Merge Gate");
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == content, Token));
        wisdom.Kind.ShouldBe(WisdomKind.Preference);
        wisdom.ScopeProjectId.ShouldBe(project.Id);
        wisdom.Reinforcement.ShouldBe(1);
        (await FromDb(db => db.Provenance.CountAsync(p => p.WisdomId == wisdom.Id, Token)))
            .ShouldBe(0, "an Episode-less save has no provenance to point at — never an all-null row");
        (await FromDb(db => db.Events.CountAsync(
                e => db.Episodes.Any(ep => ep.Id == e.EpisodeId && ep.ProjectId == project.Id), Token)))
            .ShouldBe(0, "no Remember Event lands when no Episode is live");
    }

    [Fact]
    public async Task AnUnknownDirectory_StillLands_ByCreatingItsProject()
    {
        await Context.ResetWisdomAsync(Token);
        var suffix = Guid.NewGuid().ToString("N");
        var identity = $"github.com/test/unseen-{suffix}";
        const string content = "Ship small, ship often.";

        var text = await Service().RememberAsync(
            new McpRememberRequest
            {
                ProjectIdentity = identity,
                ProjectRoot = $@"C:\roots\unseen-{suffix}",
                Cwd = $@"C:\roots\unseen-{suffix}",
                Content = content,
                Kind = "Fact",
            },
            Token);

        text.ShouldContain("Merge Gate");
        var project = await FromDb(db => db.Projects.SingleAsync(p => p.Identity == identity, Token));
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == content, Token)))
            .ScopeProjectId.ShouldBe(project.Id, "a deliberate save is never dropped (§7.1)");
    }

    [Fact]
    public async Task AnUnknownKind_NamesTheVocabulary_AndWritesNothing()
    {
        var project = await AddProjectAsync();
        await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));

        var text = await RememberAsync(project, "anything", "hunch");

        text.ShouldContain("Unknown kind 'hunch'");
        (await FromDb(db => db.Events.CountAsync(
                e => e.Type == EventType.Remember
                    && db.Episodes.Any(ep => ep.Id == e.EpisodeId && ep.ProjectId == project.Id),
                Token)))
            .ShouldBe(0);
    }

    private async Task<string> RememberAsync(Project project, string content, string kind)
        => await Service().RememberAsync(
            new McpRememberRequest
            {
                ProjectIdentity = project.Identity,
                ProjectRoot = project.RootPaths.FirstOrDefault() ?? $@"C:\roots\{project.DisplayName}",
                Cwd = @"C:\work",
                Content = content,
                Kind = kind,
            },
            Token);

    private McpRememberService Service()
    {
        var clock = new FakeTimeProvider(Now);
        return new McpRememberService(
            Context,
            new ProjectResolver(Context),
            new CaptureService(
                Context,
                new ProjectResolver(Context),
                Options.Create(new CaptureOptions()),
                clock,
                new EpisodeFeed()),
            new MergeGate(
                Context,
                _embeddings,
                new WisdomSearch(Context, Options.Create(new SearchOptions())),
                _arbiter,
                Options.Create(new DistillationOptions()),
                clock));
    }

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("mcp-remember");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Episode> AddEpisodeAsync(
        Guid projectId, DateTimeOffset startedAt, DateTimeOffset? sealedAt = null)
    {
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{Guid.NewGuid():N}",
            ProjectId = projectId,
            StartedAt = startedAt,
            SealedAt = sealedAt,
            SealReason = sealedAt is null ? null : "clear",
            Cwd = @"C:\work",
        };
        Context.Episodes.Add(episode);
        await Context.SaveChangesAsync(Token);
        return episode;
    }

    private async Task AddEventAsync(Episode episode, int seq, DateTimeOffset at)
    {
        Context.Events.Add(new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = seq,
            Type = EventType.UserPromptSubmit,
            At = at,
            Payload = """{"prompt":"earlier work"}""",
            PayloadFullSize = 10,
        });
        await Context.SaveChangesAsync(Token);
    }

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = fixture.CreateContext();
        return await query(context);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private MimirDbContext Context
    {
        get
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip(TestPostgres.SkipMessage(reason));
            }

            return _context ??= fixture.CreateContext();
        }
    }
}
