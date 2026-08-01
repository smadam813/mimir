using Bunit;
using Microsoft.EntityFrameworkCore;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Components.Pages;

namespace Mimir.Server.Tests.Components.Pages;

/// <summary>
/// The Episode screen's two jobs at the page level: hand the surface a Project it resolved once,
/// and not resolve it again for a selection. What the surface then does with the selection is
/// <c>EpisodeSurface</c>'s and is pinned there.
/// <para>
/// Postgres tier — every assertion below turns on what a real <c>ChassisBrowser</c> query answers,
/// including the one that turns on it <em>not</em> being run.
/// </para>
/// </summary>
public class EpisodePageTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private IRenderedComponent<EpisodePage> RenderAt(Guid projectId)
        => CreateRenderContext().Render<EpisodePage>(p => p.Add(c => c.ProjectId, projectId));

    /// <summary>
    /// #97's dedup, held from this side: both surface pages can be handed an id that resolves to
    /// nothing, and the notice is stated once so the two cannot drift apart. Asserted against
    /// <see cref="UnknownProject"/> rendered alone rather than against a copy of its words here,
    /// because a literal in this file would be the third statement of the same sentence.
    /// </summary>
    [Fact]
    public void AnUnresolvableId_DrawsTheSameNoticeTheOtherPageDoes()
    {
        var render = CreateRenderContext();
        var alone = render.Render<UnknownProject>();

        var page = render.Render<EpisodePage>(p => p.Add(c => c.ProjectId, Guid.CreateVersion7()));

        page.WaitForAssertion(
            () => page.Find("div.page-notice").TextContent
                .ShouldBe(alone.Find("div.page-notice").TextContent),
            TimeSpan.FromSeconds(10));
        page.FindComponents<EpisodeSurface>().ShouldBeEmpty();
    }

    /// <summary>
    /// Selecting an Episode is a route change on this page (#95), so <c>OnParametersSetAsync</c>
    /// runs again with the Project unmoved — and re-querying it there would be one wasted round
    /// trip per click, on the click a curator makes most.
    /// <para>
    /// Observed by deleting the row out from under the mounted page: a re-fetch would find nothing
    /// and swap the whole screen for the unknown-Project notice, so the surface still being there
    /// is the proof no second query ran. Reading a call count off the browser would need a fake
    /// over a concrete internal service, which is the indirection #130 decided against.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SelectingAnEpisode_DoesNotResolveTheProjectAgain()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        var page = RenderAt(project.Id);
        page.WaitForAssertion(
            () => page.FindComponents<EpisodeSurface>().ShouldHaveSingleItem(),
            TimeSpan.FromSeconds(10));

        await Context.Episodes.Where(e => e.Id == episode.Id).ExecuteDeleteAsync(Token);
        await Context.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(Token);
        page.Render(p => p
            .Add(c => c.ProjectId, project.Id)
            .Add(c => c.EpisodeId, episode.Id));
        // Settled, because a second query would land on a later render: asserting straight after
        // the parameter set would read the screen before the regression could reach it.
        await page.SettleAsync();

        page.FindComponents<EpisodeSurface>().ShouldHaveSingleItem();
        page.FindAll("div.page-notice").ShouldBeEmpty();
    }

    /// <summary>
    /// The other edge of the same guard: a sidebar switch <em>is</em> a different Project, so that
    /// one has to re-resolve. Without this the guard above would be satisfied by never querying
    /// twice at all, and the page would show the outgoing Project's name for the rest of the circuit.
    /// </summary>
    [Fact]
    public async Task SwitchingProject_ResolvesTheIncomingOne()
    {
        var outgoing = await AddProjectAsync("outgoing");
        var page = RenderAt(outgoing.Id);
        page.WaitForAssertion(
            () => page.FindComponents<EpisodeSurface>().ShouldHaveSingleItem(),
            TimeSpan.FromSeconds(10));

        page.Render(p => p.Add(c => c.ProjectId, Guid.CreateVersion7()));

        page.WaitForAssertion(
            () => page.Find("div.page-notice h1").TextContent.ShouldBe("Unknown Project"),
            TimeSpan.FromSeconds(10));
    }
}
