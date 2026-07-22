using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Contracts.Mcp;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// <c>mimir_search</c> (§7) against a real Postgres: fused Wisdom + Episode results, deliberate
/// reach beyond the ambient universe (other Projects' Wisdom, Retired only on request), the
/// documented filters, and the §3 logging rule — a non-empty answer logs lane=MCP with the query
/// as <c>query_context</c>, an empty one leaves no trace.
/// </summary>
public sealed class McpSearchServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>No word overlap with the test Wisdom, so only the vector leg ranks Wisdom;
    /// Episode payloads deliberately contain "deploy…pipeline" so the FTS leg finds them.</summary>
    private const string Query = "how do I deploy the pipeline?";

    private readonly FakeEmbeddings _embeddings = new();

    private MimirDbContext? _context;

    public ValueTask InitializeAsync()
    {
        _embeddings.Map(Query, TestVectors.Basis);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task FusedResults_ReachOtherProjectsWisdom_AndEpisodeEvents_AndLogTheInjection()
    {
        await Context.ResetWisdomAsync(Token);
        var (requester, other) = (await AddProjectAsync(), await AddProjectAsync());
        var foreign = await AddWisdomAsync(other.Id, "unrelated filler one", cosine: 0.9);
        var episode = await AddEpisodeAsync(requester.Id);
        await AddEventAsync(episode, seq: 1, "let us deploy the pipeline today");
        var sessionId = NewMcpSessionId();

        var text = await SearchAsync(requester, new() { SessionId = sessionId });

        text.ShouldContain(foreign.Text, customMessage: "MCP reaches other Projects' Wisdom (§7)");
        text.ShouldContain(other.DisplayName);
        text.ShouldContain(episode.SessionId);
        text.ShouldContain("deploy the pipeline today");

        var logged = await FromDb(db => db.Injections.SingleAsync(i => i.SessionId == sessionId, Token));
        logged.Lane.ShouldBe(InjectionLane.Mcp);
        logged.QueryContext.ShouldBe(Query, customMessage: "MCP rows carry the tool query (§3)");
        logged.ProjectId.ShouldBe(requester.Id);
        logged.Chars.ShouldBe(text.Length);
        logged.Items.Select(i => i.WisdomId).ShouldBe([foreign.Id]);
    }

    [Fact]
    public async Task RetiredWisdom_SurfacesOnlyWithIncludeRetired_AndIsMarked()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var retired = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        retired.RetiredAt = Now.AddDays(-1);
        await Context.SaveChangesAsync(Token);

        var withoutFlag = await SearchAsync(project, new() { IncludeEpisodes = false });
        var withFlag = await SearchAsync(project, new() { IncludeEpisodes = false, IncludeRetired = true });

        withoutFlag.ShouldNotContain(retired.Text, customMessage: "Retired is unreachable by default (§7)");
        withFlag.ShouldContain(retired.Text);
        withFlag.ShouldContain("Retired 2026-07-21");
    }

    [Fact]
    public async Task KindAndSinceFilters_KeepOnlyMatchingWisdom()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var lesson = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9, WisdomKind.Lesson);
        var staleFact = await AddWisdomAsync(
            project.Id, "unrelated filler two", cosine: 0.8, confirmedAt: Now.AddDays(-30));

        var byKind = await SearchAsync(project, new() { Kind = "lesson", IncludeEpisodes = false });
        var bySince = await SearchAsync(project, new() { Since = Now.AddDays(-7), IncludeEpisodes = false });

        byKind.ShouldContain(lesson.Text);
        byKind.ShouldNotContain(staleFact.Text);
        bySince.ShouldContain(lesson.Text);
        bySince.ShouldNotContain(staleFact.Text, customMessage: "since gates on last_confirmed_at");
    }

    [Fact]
    public async Task ProjectFilter_NarrowsBothLegs_AndAMissNamesTheKnownProjects()
    {
        await Context.ResetWisdomAsync(Token);
        var (mine, other) = (await AddProjectAsync(), await AddProjectAsync());
        var foreign = await AddWisdomAsync(other.Id, "unrelated filler one", cosine: 0.9);
        var mineWisdom = await AddWisdomAsync(mine.Id, "unrelated filler two", cosine: 0.8);
        var otherEpisode = await AddEpisodeAsync(other.Id);
        await AddEventAsync(otherEpisode, seq: 1, "they deploy the pipeline elsewhere");
        var myEpisode = await AddEpisodeAsync(mine.Id);
        await AddEventAsync(myEpisode, seq: 1, "we deploy the pipeline here");

        var filtered = await SearchAsync(mine, new() { Project = mine.DisplayName });
        var missed = await SearchAsync(mine, new() { Project = "no-such-project" });

        filtered.ShouldContain(mineWisdom.Text);
        filtered.ShouldNotContain(foreign.Text);
        filtered.ShouldContain(myEpisode.SessionId);
        filtered.ShouldNotContain(otherEpisode.SessionId);
        missed.ShouldContain("No project matches 'no-such-project'");
        missed.ShouldContain(mine.DisplayName, customMessage: "a miss offers the known names back");
    }

    [Fact]
    public async Task IncludeEpisodesFalse_SkipsTheEpisodeLeg()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var episode = await AddEpisodeAsync(project.Id);
        await AddEventAsync(episode, seq: 1, "we deploy the pipeline here");

        var text = await SearchAsync(project, new() { IncludeEpisodes = false });

        text.ShouldNotContain(episode.SessionId);
        text.ShouldNotContain("Episode events");
    }

    [Fact]
    public async Task NoMatches_AnswersPlainly_AndLogsNothing()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.5);
        var sessionId = NewMcpSessionId();

        // Nothing crosses either leg: the lone Wisdom ranks (its cosine is real) but MCP has no
        // cosine gate — so force emptiness the honest way, an off-vocabulary kind filter.
        var text = await SearchAsync(
            project, new() { SessionId = sessionId, Kind = "Procedure", IncludeEpisodes = false });

        text.ShouldBe($"No Wisdom or Episode matches for \"{Query}\".");
        (await FromDb(db => db.Injections.CountAsync(i => i.SessionId == sessionId, Token)))
            .ShouldBe(0, "an empty answer leaves no Injection row (§7)");
    }

    [Fact]
    public async Task AnUnknownKind_NamesTheVocabulary()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();

        var text = await SearchAsync(project, new() { Kind = "hunch" });

        text.ShouldContain("Unknown kind 'hunch'");
        text.ShouldContain("Fact, Preference, Lesson, Procedure");
    }

    /// <summary>Overridable request defaults, merged over the requester's §7.1 resolution.</summary>
    private sealed record Overrides
    {
        public string? SessionId { get; init; }

        public string? Project { get; init; }

        public string? Kind { get; init; }

        public DateTimeOffset? Since { get; init; }

        public bool IncludeEpisodes { get; init; } = true;

        public bool IncludeRetired { get; init; }
    }

    private async Task<string> SearchAsync(Project requester, Overrides overrides)
    {
        var service = new McpSearchService(
            Context,
            new QueryRanking(
                Context,
                _embeddings,
                new WisdomSearch(Context, Options.Create(new SearchOptions())),
                Options.Create(new RecallOptions()),
                new FakeTimeProvider(Now)),
            new EventSearch(Context, Options.Create(new SearchOptions())),
            new McpProjects(Context),
            new FakeTimeProvider(Now));
        return await service.SearchAsync(
            new McpSearchRequest
            {
                SessionId = overrides.SessionId ?? NewMcpSessionId(),
                ProjectIdentity = requester.Identity,
                ProjectRoot = requester.RootPaths.FirstOrDefault() ?? $@"C:\roots\{requester.DisplayName}",
                Query = Query,
                Project = overrides.Project,
                Kind = overrides.Kind,
                Since = overrides.Since,
                IncludeEpisodes = overrides.IncludeEpisodes,
                IncludeRetired = overrides.IncludeRetired,
            },
            Token);
    }

    private static string NewMcpSessionId() => $"mcp-{Guid.NewGuid():N}";

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("mcp");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Wisdom> AddWisdomAsync(
        Guid scopeProjectId,
        string text,
        double cosine,
        WisdomKind kind = WisdomKind.Fact,
        DateTimeOffset? confirmedAt = null)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(TestVectors.WithCosine(cosine)),
            Reinforcement = 1,
            LastConfirmedAt = confirmedAt ?? Now,
        };
        Context.Wisdom.Add(wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private async Task<Episode> AddEpisodeAsync(Guid projectId)
    {
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{Guid.NewGuid():N}",
            ProjectId = projectId,
            StartedAt = Now.AddHours(-1),
            Cwd = @"C:\work",
        };
        Context.Episodes.Add(episode);
        await Context.SaveChangesAsync(Token);
        return episode;
    }

    private async Task AddEventAsync(Episode episode, int seq, string promptText)
    {
        Context.Events.Add(new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = seq,
            Type = EventType.UserPromptSubmit,
            At = Now.AddMinutes(-30),
            Payload = $$"""{"prompt":"{{promptText}}"}""",
            PayloadFullSize = promptText.Length,
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
