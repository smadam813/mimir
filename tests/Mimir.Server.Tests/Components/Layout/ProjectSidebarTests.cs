using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Layout;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Components.Layout;

/// <summary>
/// The sidebar's second group — the one that swaps to match whichever surface is on screen — and
/// the rule that decides whether its rows are links or figures. <c>ChassisBrowserTests</c> pins
/// what the queries answer; what is here is which of those answers gets rendered, and as what.
/// <para>
/// Postgres tier: the Project list and all three attention groups are queries, and the group that
/// renders is chosen from a URL the test drives.
/// </para>
/// </summary>
public class ProjectSidebarTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private (BunitContext Render, NavigationManager Nav) NewCircuit()
    {
        var render = CreateRenderContext();
        return (render, render.Services.GetRequiredService<NavigationManager>());
    }

    /// <summary>
    /// A real <see cref="CascadingValue{TValue}"/> through the render tree rather than bUnit's
    /// <c>AddCascadingValue</c>, whose value is constrained <c>notnull</c> — and null is one of the
    /// three states the sidebar has to answer for.
    /// </summary>
    private static IRenderedComponent<ProjectSidebar> RenderUnder(BunitContext render, bool? isFirstRun)
    {
        render.RenderTree.Add<CascadingValue<bool?>>(p => p
            .Add(c => c.Name, MainLayout.FirstRunCascade)
            .Add(c => c.Value, isFirstRun));
        return render.Render<ProjectSidebar>();
    }

    private static IRenderedComponent<ProjectSidebar> RenderAt(
        BunitContext render, NavigationManager nav, string relativePath, bool? isFirstRun = false)
    {
        nav.NavigateTo(relativePath);
        return RenderUnder(render, isFirstRun);
    }

    /// <summary>
    /// Global sits on top — it is the universe every other Project reads through, so a list that
    /// sorted it in by name would bury it somewhere alphabetical.
    /// </summary>
    [Fact]
    public async Task TheProjectList_PutsGlobalFirst()
    {
        await AddProjectAsync("alpha");
        var (render, nav) = NewCircuit();

        var sidebar = RenderAt(render, nav, "");

        sidebar.WaitForAssertion(
            () => sidebar.FindAll("a.sidebar-item span.sidebar-item-name")
                .Select(n => n.TextContent).ShouldBe(["Global", "alpha"]),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A "Needs attention" row is a link exactly where the tab body can read the filter back off
    /// the URL. Wisdom's three can — its surface takes the lens from the query string — so those
    /// are anchors carrying that lens. The Capture and Recall groups cannot, and a row promising
    /// "Failed" that landed on an unfiltered list would be a worse answer than a figure.
    /// </summary>
    [Theory]
    [InlineData("wisdom", true)]
    [InlineData("episodes", false)]
    [InlineData("injections", false)]
    public async Task AnAttentionRow_IsALinkOnlyWhereTheTabCanReadTheFilterBack(string tab, bool linked)
    {
        var project = await AddProjectAsync();
        var (render, nav) = NewCircuit();

        var sidebar = RenderAt(render, nav, $"projects/{project.Id}/{tab}");

        sidebar.WaitForAssertion(
            () => sidebar.FindAll("div.sidebar-group").Count.ShouldBe(2),
            TimeSpan.FromSeconds(10));
        var attention = sidebar.FindAll("div.sidebar-group")[1];
        attention.QuerySelectorAll("span.attention-dot").Length.ShouldBe(3);
        attention.QuerySelectorAll("a.sidebar-item").Length.ShouldBe(linked ? 3 : 0);
    }

    /// <summary>
    /// And the link is the Wisdom listing narrowed to that lens — the same screen the tab already
    /// shows, not a filter view of its own.
    /// </summary>
    [Fact]
    public async Task TheWisdomAttentionRows_LinkToTheListingUnderTheirOwnLens()
    {
        var project = await AddProjectAsync();
        var (render, nav) = NewCircuit();

        var sidebar = RenderAt(render, nav, $"projects/{project.Id}/wisdom");

        sidebar.WaitForAssertion(
            () => sidebar.FindAll("div.sidebar-group")[1]
                .QuerySelectorAll("a.sidebar-item")
                .Select(a => a.GetAttribute("href"))
                .ShouldBe(
                [
                    $"projects/{project.Id}/wisdom?show=contested",
                    $"projects/{project.Id}/wisdom?show=orphaned",
                    $"projects/{project.Id}/wisdom?show=retired",
                ]),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// On first run the second group has nothing to count for, and the one thing worth saying is
    /// that there is nothing to configure: a Project appears on its session's first hook.
    /// </summary>
    [Fact]
    public void OnFirstRun_TheSecondGroupGivesWayToTheNoteAboutHooks()
    {
        var (render, nav) = NewCircuit();

        var sidebar = RenderAt(render, nav, "", isFirstRun: true);

        sidebar.WaitForAssertion(
            () => sidebar.Find("p.sidebar-note").TextContent
                .ShouldContain("appear here on their session's first hook"),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A null cascade reads as "not first run" here, unlike in the header: the group it would
    /// replace is the one this sidebar renders in every other state anyway, so waiting for the
    /// answer would only mean a group that appears a beat late.
    /// </summary>
    [Fact]
    public async Task WhileFirstRunIsUnknown_TheSidebarRendersItsOrdinarySecondGroup()
    {
        var project = await AddProjectAsync();
        var (render, nav) = NewCircuit();

        var sidebar = RenderAt(render, nav, $"projects/{project.Id}/episodes", isFirstRun: null);

        sidebar.WaitForAssertion(
            () => sidebar.FindAll("h6.sidebar-heading").Select(h => h.TextContent)
                .ShouldBe(["Projects", "Capture"]),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Global is a Project row like any other here, so landing on its Episodes tab still selects
    /// it — the sidebar tells "selected" from the URL rather than from anything the surface says.
    /// </summary>
    [Fact]
    public void TheSelectedRow_IsTheOneTheUrlNames()
    {
        var (render, nav) = NewCircuit();

        var sidebar = RenderAt(render, nav, $"projects/{Project.GlobalId}/episodes");

        sidebar.WaitForAssertion(
            () => sidebar.FindAll("a.sidebar-item.is-selected")
                .Select(a => a.QuerySelector("span.sidebar-item-name")!.TextContent)
                .ShouldBe(["Global"]),
            TimeSpan.FromSeconds(10));
    }
}
