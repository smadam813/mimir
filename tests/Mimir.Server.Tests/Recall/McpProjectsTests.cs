using Microsoft.EntityFrameworkCore;
using Mimir.Server.Recall;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The Project lookups the MCP tools share. The filter half is pinned through the tools that use
/// it (<see cref="McpSearchServiceTests"/>, <see cref="McpTimelineServiceTests"/>); what only this
/// class can reach is the requester half's refusal to create.
/// </summary>
public sealed class McpProjectsTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task TheRequesterLookup_FindsAProjectByIdentity_AndByRootPath()
    {
        var project = await AddProjectAsync("mcp-projects");
        var lookups = new McpProjects(Context);

        var byIdentity = await lookups.FindRequesterAsync(
            project.Identity, @"C:\somewhere\else", Token);
        var byRoot = await lookups.FindRequesterAsync(
            "github.com/test/not-this-one", project.RootPaths[0], Token);

        byIdentity.ShouldNotBeNull().Id.ShouldBe(project.Id);
        byRoot.ShouldNotBeNull().Id.ShouldBe(
            project.Id, "the root-path leg is what finds a repository whose remote has moved");
    }

    [Fact]
    public async Task TheRequesterLookup_ForAnUnknownDirectory_MintsNoProject()
    {
        await AddProjectAsync("mcp-projects");
        var before = await FromDb(db => db.Projects.CountAsync(Token));

        var found = await new McpProjects(Context).FindRequesterAsync(
            Identity("never-seeded"), Root("C", "never-seeded"), Token);

        found.ShouldBeNull();
        (await FromDb(db => db.Projects.CountAsync(Token)))
            .ShouldBe(before, "a search from an unknown directory earns no affinity, not a Project");
    }
}
