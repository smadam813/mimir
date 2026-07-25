using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Mcp;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// <c>mimir_search</c> (§7) against a real Postgres: fused Wisdom + Episode results, deliberate
/// reach beyond the ambient universe (other Projects' Wisdom, Retired only on request), the
/// documented filters, and the §3 logging rule — a non-empty answer logs lane=MCP with the query
/// as <c>query_context</c>, an empty one leaves no trace.
/// </summary>
public sealed class McpSearchServiceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>No word overlap with the test Wisdom, so only the vector leg ranks Wisdom;
    /// Episode payloads deliberately contain "deploy…pipeline" so the FTS leg finds them.</summary>
    private const string Query = "how do I deploy the pipeline?";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Embeddings.Map(Query, TestVectors.Basis);
    }

    [Fact]
    public async Task FusedResults_ReachOtherProjectsWisdom_AndEpisodeEvents_AndLogTheInjection()
    {
        var (requester, other) = (await AddProjectAsync("mcp"), await AddProjectAsync("mcp"));
        var foreign = await AddWisdomAsync(other.Id, "unrelated filler one", cosine: 0.9);
        var episode = await AddEpisodeAsync(requester.Id);
        await AddPromptEventAsync(episode.Id, "let us deploy the pipeline today");
        var sessionId = NewMcpSessionId();

        var text = await SearchAsync(requester, new() { SessionId = sessionId });

        text.ShouldContain(foreign.Text, customMessage: "MCP reaches other Projects' Wisdom (§7)");
        text.ShouldContain(other.DisplayName);
        text.ShouldContain(episode.SessionId);
        text.ShouldContain("deploy the pipeline today");

        var logged = await FromDb(db => db.Injections.SingleAsync(Token));
        logged.SessionId.ShouldBe(sessionId);
        logged.Lane.ShouldBe(InjectionLane.Mcp);
        logged.QueryContext.ShouldBe(Query, customMessage: "MCP rows carry the tool query (§3)");
        logged.ProjectId.ShouldBe(requester.Id);
        logged.Chars.ShouldBe(text.Length);
        logged.Items.Select(i => i.WisdomId).ShouldBe([foreign.Id]);
    }

    [Fact]
    public async Task RetiredWisdom_SurfacesOnlyWithIncludeRetired_AndIsMarked()
    {
        var project = await AddProjectAsync("mcp");
        var retired = await AddWisdomAsync(
            project.Id, "unrelated filler one", cosine: 0.9, retiredAt: Now.AddDays(-1));

        var withoutFlag = await SearchAsync(project, new() { IncludeEpisodes = false });
        var withFlag = await SearchAsync(project, new() { IncludeEpisodes = false, IncludeRetired = true });

        withoutFlag.ShouldNotContain(retired.Text, customMessage: "Retired is unreachable by default (§7)");
        withFlag.ShouldContain(retired.Text);
        withFlag.ShouldContain("Retired 2026-07-21");
    }

    [Fact]
    public async Task KindAndSinceFilters_KeepOnlyMatchingWisdom()
    {
        var project = await AddProjectAsync("mcp");
        var lesson = await AddWisdomAsync(
            project.Id, "unrelated filler one", cosine: 0.9, kind: WisdomKind.Lesson);
        var staleFact = await AddWisdomAsync(
            project.Id, "unrelated filler two", cosine: 0.8, lastConfirmedAt: Now.AddDays(-30));

        var byKind = await SearchAsync(project, new() { Kind = "lesson", IncludeEpisodes = false });
        var bySince = await SearchAsync(project, new() { Since = Now.AddDays(-7), IncludeEpisodes = false });

        byKind.ShouldContain(lesson.Text);
        byKind.ShouldNotContain(staleFact.Text);
        bySince.ShouldContain(lesson.Text);
        bySince.ShouldNotContain(staleFact.Text, customMessage: "since gates on last_confirmed_at");
    }

    [Fact]
    public async Task AFilter_FindsMatchesTheUnfilteredTopNWouldHaveCrowdedOut()
    {
        var project = await AddProjectAsync("mcp");
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var lesson = await AddWisdomAsync(
            project.Id, "unrelated filler two", cosine: 0.8, kind: WisdomKind.Lesson);

        // PerLegTopN = 1: the unfiltered pool holds only the Fact. The kind filter must apply
        // in SQL, before that limit, so the Lesson still surfaces — never a false "no matches".
        var text = await SearchAsync(
            project,
            new() { Kind = "Lesson", IncludeEpisodes = false },
            new SearchOptions { PerLegTopN = 1 });

        text.ShouldContain(lesson.Text, customMessage: "filters run pre-limit in the search SQL");
    }

    [Fact]
    public async Task ProjectFilter_NarrowsBothLegs_AndAMissNamesTheKnownProjects()
    {
        var (mine, other) = (await AddProjectAsync("mcp"), await AddProjectAsync("mcp"));
        var foreign = await AddWisdomAsync(other.Id, "unrelated filler one", cosine: 0.9);
        var mineWisdom = await AddWisdomAsync(mine.Id, "unrelated filler two", cosine: 0.8);
        var otherEpisode = await AddEpisodeAsync(other.Id);
        await AddPromptEventAsync(otherEpisode.Id, "they deploy the pipeline elsewhere");
        var myEpisode = await AddEpisodeAsync(mine.Id);
        await AddPromptEventAsync(myEpisode.Id, "we deploy the pipeline here");

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
        var project = await AddProjectAsync("mcp");
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var episode = await AddEpisodeAsync(project.Id);
        await AddPromptEventAsync(episode.Id, "we deploy the pipeline here");

        var text = await SearchAsync(project, new() { IncludeEpisodes = false });

        text.ShouldNotContain(episode.SessionId);
        text.ShouldNotContain("Episode events");
    }

    [Fact]
    public async Task NoMatches_AnswersPlainly_AndLogsNothing()
    {
        var project = await AddProjectAsync("mcp");
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.5);

        // Nothing crosses either leg: the lone Wisdom ranks (its cosine is real) but MCP has no
        // cosine gate — so force emptiness the honest way, an off-vocabulary kind filter.
        var text = await SearchAsync(
            project, new() { Kind = "Procedure", IncludeEpisodes = false });

        text.ShouldBe($"No Wisdom or Episode matches for \"{Query}\".");
        (await FromDb(db => db.Injections.CountAsync(Token)))
            .ShouldBe(0, "an empty answer leaves no Injection row (§7)");
    }

    [Fact]
    public async Task AnUnknownKind_NamesTheVocabulary()
    {
        var project = await AddProjectAsync("mcp");

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

    private async Task<string> SearchAsync(
        Project requester, Overrides overrides, SearchOptions? searchOptions = null)
    {
        var service = new McpSearchService(
            Context,
            new QueryRanking(
                Context,
                Embeddings,
                new WisdomSearch(Context, Options.Create(searchOptions ?? new SearchOptions())),
                Options.Create(new RecallOptions()),
                Clock),
            new EventSearch(Context),
            new McpProjects(Context),
            Clock);
        return await service.SearchAsync(
            new McpSearchRequest
            {
                SessionId = overrides.SessionId ?? NewMcpSessionId(),
                ProjectIdentity = requester.Identity,
                ProjectRoot = requester.RootPaths[0],
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

    private async Task AddPromptEventAsync(Guid episodeId, string promptText)
        => await AddEventAsync(
            episodeId,
            seq: 1,
            at: Now.AddMinutes(-30),
            payload: $$"""{"prompt":"{{promptText}}"}""");
}
