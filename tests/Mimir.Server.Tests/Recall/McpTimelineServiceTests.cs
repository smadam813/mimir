using Microsoft.EntityFrameworkCore;
using Mimir.Contracts.Mcp;
using Mimir.Server.Recall;

namespace Mimir.Server.Tests.Recall;

public sealed class McpTimelineServiceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Timeline_ListsNewestFirst_WithSealState()
    {
        var project = await AddProjectAsync("mcp-timeline");
        var sealedEpisode = await AddEpisodeAsync(
            project.Id, startedAt: Now.AddHours(-3), sealedAt: Now.AddHours(-2));
        var live = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));

        var text = await TimelineAsync(new() { Project = project.DisplayName });

        text.ShouldContain(live.SessionId);
        text.ShouldContain(sealedEpisode.SessionId);
        text.IndexOf(live.SessionId, StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf(sealedEpisode.SessionId, StringComparison.Ordinal));
        text.ShouldContain("· live");
        text.ShouldContain("sealed 2026-07-22 10:00Z (clear)");
    }

    [Fact]
    public async Task ProjectAndSinceFilters_NarrowTheTimeline()
    {
        var (mine, other) = (await AddProjectAsync("mine"), await AddProjectAsync("theirs"));
        var recent = await AddEpisodeAsync(mine.Id, startedAt: Now.AddHours(-1));
        var old = await AddEpisodeAsync(mine.Id, startedAt: Now.AddDays(-10));
        var foreign = await AddEpisodeAsync(other.Id, startedAt: Now);

        var text = await TimelineAsync(
            new() { Project = mine.DisplayName, Since = Now.AddDays(-1) });

        text.ShouldContain(recent.SessionId);
        text.ShouldNotContain(old.SessionId);
        text.ShouldNotContain(foreign.SessionId);
    }

    /// <summary>
    /// A <c>since</c> in the caller's own offset. Every other test here hands one already in UTC,
    /// which is the one shape the service's normalization is invisible to — an MCP client parsing
    /// what a human typed produces this one. Two failures are in reach and the fixture wants both:
    /// Npgsql refuses a non-UTC <c>DateTimeOffset</c> against <c>timestamptz</c> outright, and a
    /// normalization that read the wall clock instead of the instant would shift the cut by the
    /// offset — so the bound sits two hours out, inside the seven the offset would move it.
    /// </summary>
    [Fact]
    public async Task ANonUtcSince_CutsOnTheInstant()
    {
        var project = await AddProjectAsync("mcp-timeline");
        var after = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-1));
        var before = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-3));

        var text = await TimelineAsync(
            new() { Since = Now.AddHours(-2).ToOffset(TimeSpan.FromHours(-7)) });

        text.ShouldContain(after.SessionId);
        text.ShouldNotContain(before.SessionId);
    }

    [Fact]
    public async Task AnUnknownProject_NamesTheKnownOnes()
    {
        var project = await AddProjectAsync("mcp-timeline");

        var text = await TimelineAsync(new() { Project = "no-such-project" });

        text.ShouldContain("No project matches 'no-such-project'");
        text.ShouldContain(project.DisplayName);
    }

    [Fact]
    public async Task Timeline_RecallsNoWisdom_SoItLogsNoInjectionRow()
    {
        var project = await AddProjectAsync("mcp-timeline");
        var episode = await AddEpisodeAsync(project.Id);

        var text = await TimelineAsync(new());

        text.ShouldContain(episode.SessionId, customMessage: "the timeline answered with content");
        (await FromDb(db => db.Injections.CountAsync(Token)))
            .ShouldBe(0, "nothing a timeline returns is Wisdom, so no injection happened (§7)");
    }

    private async Task<string> TimelineAsync(McpTimelineRequest request)
        => await new McpTimelineService(Context, new McpProjects(Context))
            .TimelineAsync(request, Token);
}
