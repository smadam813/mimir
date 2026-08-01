using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.FirstRun;
using Mimir.Server.Components.Layout;

namespace Mimir.Server.Tests.Components.Layout;

/// <summary>
/// The chassis's two decisions: whether this install is still being introduced to Claude Code, and
/// how the body sits under whatever route is on screen. Both were prose above the markup until
/// now, and both are shell-wide — a regression in either is visible on every page at once, which
/// is exactly the class of bug nobody writes a unit test for.
/// <para>
/// Postgres tier: first run is a real query (<c>ChassisBrowser.IsFirstRunAsync</c>), so "no
/// non-Global Project exists" is seeded rather than stubbed.
/// </para>
/// </summary>
public class MainLayoutTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private const string BodyMarker = "the-routed-body";

    private (BunitContext Render, NavigationManager Nav) NewCircuit()
    {
        var render = CreateRenderContext();
        return (render, render.Services.GetRequiredService<NavigationManager>());
    }

    private static IRenderedComponent<MainLayout> RenderShell(BunitContext render)
        => render.Render<MainLayout>(p => p.Add(
            c => c.Body, b => b.AddMarkupContent(0, $"<p id=\"{BodyMarker}\">routed</p>")));

    /// <summary>
    /// First run is a state of the same shell, not a screen of its own: the header and sidebar the
    /// curator will keep using are still there, the tab strip goes — there is no Project to count
    /// anything for — and the body is replaced outright rather than rendering the route underneath.
    /// </summary>
    [Fact]
    public void OnFirstRun_TheShellSwapsItsBodyAndDropsTheTabStrip()
    {
        var (render, _) = NewCircuit();

        var shell = RenderShell(render);

        shell.WaitForAssertion(
            () => shell.FindComponents<FirstRunPanel>().ShouldHaveSingleItem(),
            Patience);
        shell.FindComponents<SurfaceTabStrip>().ShouldBeEmpty();
        shell.FindAll($"#{BodyMarker}").ShouldBeEmpty();
        shell.FindComponents<AppHeader>().ShouldHaveSingleItem();
        shell.FindComponents<ProjectSidebar>().ShouldHaveSingleItem();
    }

    /// <summary>
    /// One Project — any Project other than the §3 Global pseudo-project — and the shell is the
    /// ordinary one: the strip is back and the router's body renders where the panel was.
    /// </summary>
    [Fact]
    public async Task OnceAProjectExists_TheRoutedBodyRendersUnderTheFullChassis()
    {
        await AddProjectAsync();
        var (render, _) = NewCircuit();

        var shell = RenderShell(render);

        shell.WaitForAssertion(
            () => shell.FindAll($"#{BodyMarker}").ShouldHaveSingleItem(),
            Patience);
        shell.FindComponents<FirstRunPanel>().ShouldBeEmpty();
        shell.FindComponents<SurfaceTabStrip>().ShouldHaveSingleItem();
    }

    /// <summary>
    /// The body's fit is read off the URL, not off the page that rendered into it. A surface fills
    /// the frame flush and scrolls its own panes; everything else takes the plain scrolling body.
    /// </summary>
    [Theory]
    [InlineData("projects/9e1d3e60-0000-0000-0000-000000000001/wisdom", true)]
    [InlineData("projects/9e1d3e60-0000-0000-0000-000000000001/episodes/9e1d3e60-0000-0000-0000-000000000002", true)]
    [InlineData("projects/9e1d3e60-0000-0000-0000-000000000001", true)]
    [InlineData("projects/9e1d3e60-0000-0000-0000-000000000001/briefs", true)]
    [InlineData("", false)]
    [InlineData("Error", false)]
    public async Task TheBodyIsFlush_ExactlyOnTheSurfaceRoutes(string relativePath, bool flush)
    {
        await AddProjectAsync();
        var (render, nav) = NewCircuit();
        nav.NavigateTo(relativePath);

        var shell = RenderShell(render);

        shell.WaitForAssertion(
            () => shell.Find("main.app-body").ClassList.Contains("is-flush").ShouldBe(flush),
            Patience);
    }

    /// <summary>
    /// The unrecognised-tab case above is the one worth naming: a <c>projects/{id}</c> URL always
    /// names a surface, because <c>ProjectRoute.Parse</c> folds a missing or unknown tab onto
    /// Episodes — so an id that resolves to nothing still lands in the flush body, and the notice
    /// the page draws there has to wear <c>.page-notice</c> rather than assume padding.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableProjectUrl_StillTakesTheFlushBody()
    {
        await AddProjectAsync();
        var (render, nav) = NewCircuit();
        nav.NavigateTo($"projects/{Guid.CreateVersion7()}/episodes");

        var shell = RenderShell(render);

        shell.WaitForAssertion(
            () => shell.Find("main.app-body").ClassList.ShouldContain("is-flush"),
            Patience);
    }

    /// <summary>
    /// First run outranks the URL: the panel scrolls as one document, so the shell must not hand it
    /// a flush body even when the curator lands on a surface URL — which they can, since a deep
    /// link survives a database that has since been emptied.
    /// </summary>
    [Fact]
    public void OnFirstRun_TheBodyIsNeverFlushWhateverTheUrlSays()
    {
        var (render, nav) = NewCircuit();
        nav.NavigateTo($"projects/{Guid.CreateVersion7()}/wisdom");

        var shell = RenderShell(render);

        shell.WaitForAssertion(
            () =>
            {
                shell.FindComponents<FirstRunPanel>().ShouldHaveSingleItem();
                shell.Find("main.app-body").ClassList.ShouldNotContain("is-flush");
            },
            Patience);
    }
}
