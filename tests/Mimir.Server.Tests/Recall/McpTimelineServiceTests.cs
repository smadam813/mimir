using Mimir.Contracts.Mcp;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// <c>mimir_timeline</c> (§7) against a real Postgres: Episodes newest first, each carrying its
/// seal state — live, or sealed with the §4 reason — narrowed by the project and since filters.
/// </summary>
public sealed class McpTimelineServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

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
    public async Task Timeline_ListsNewestFirst_WithSealState()
    {
        var project = await AddProjectAsync();
        var sealedEpisode = await AddEpisodeAsync(
            project.Id, startedAt: Now.AddHours(-3), sealedAt: Now.AddHours(-2), reason: "clear");
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
        var (mine, other) = (await AddProjectAsync(), await AddProjectAsync());
        var recent = await AddEpisodeAsync(mine.Id, startedAt: Now.AddHours(-1));
        var old = await AddEpisodeAsync(mine.Id, startedAt: Now.AddDays(-10));
        var foreign = await AddEpisodeAsync(other.Id, startedAt: Now);

        var text = await TimelineAsync(
            new() { Project = mine.DisplayName, Since = Now.AddDays(-1) });

        text.ShouldContain(recent.SessionId);
        text.ShouldNotContain(old.SessionId);
        text.ShouldNotContain(foreign.SessionId);
    }

    [Fact]
    public async Task AnUnknownProject_NamesTheKnownOnes()
    {
        var project = await AddProjectAsync();

        var text = await TimelineAsync(new() { Project = "no-such-project" });

        text.ShouldContain("No project matches 'no-such-project'");
        text.ShouldContain(project.DisplayName);
    }

    private async Task<string> TimelineAsync(McpTimelineRequest request)
        => await new McpTimelineService(Context, new McpProjects(Context))
            .TimelineAsync(request, Token);

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("mcp-timeline");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Episode> AddEpisodeAsync(
        Guid projectId, DateTimeOffset startedAt, DateTimeOffset? sealedAt = null, string? reason = null)
    {
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{Guid.NewGuid():N}",
            ProjectId = projectId,
            StartedAt = startedAt,
            SealedAt = sealedAt,
            SealReason = reason,
            Cwd = @"C:\work",
        };
        Context.Episodes.Add(episode);
        await Context.SaveChangesAsync(Token);
        return episode;
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
