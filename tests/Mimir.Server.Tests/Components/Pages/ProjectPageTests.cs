using Bunit;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Components.Injections;
using Mimir.Server.Components.Pages;

namespace Mimir.Server.Tests.Components.Pages;

/// <summary>
/// What is left of the tab switch after #91 and #95 took Wisdom and Episodes out of it, pinned as
/// the branch each URL renders rather than as the prose that used to sit above it.
/// <see cref="PageRoutesTests"/> pins why Wisdom and Episodes cannot arrive here at all; this pins
/// what the two cases that can arrive actually draw.
/// <para>
/// Postgres tier: the page resolves its Project through <c>ChassisBrowser</c> before it renders
/// anything, so an unresolvable id is a real query returning no row rather than a parameter.
/// </para>
/// </summary>
public class ProjectPageTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private IRenderedComponent<ProjectPage> RenderAt(Guid projectId, string? tab)
        => CreateRenderContext().Render<ProjectPage>(p => p
            .Add(c => c.ProjectId, projectId)
            .Add(c => c.Tab, tab));

    [Fact]
    public async Task TheInjectionsTab_RendersTheInjectionLog()
    {
        var project = await AddProjectAsync();

        var page = RenderAt(project.Id, "injections");

        page.WaitForAssertion(
            () => page.FindComponents<InjectionLogTab>().ShouldHaveSingleItem(),
            Patience);
        page.FindComponents<EpisodeSurface>().ShouldBeEmpty();
    }

    /// <summary>
    /// The tabless <c>/projects/{id}</c> route. Episodes is the landing surface, so this is the
    /// branch a sidebar row hits before any tab has been chosen.
    /// </summary>
    [Fact]
    public async Task TheTablessRoute_LandsOnEpisodes()
    {
        var project = await AddProjectAsync();

        var page = RenderAt(project.Id, tab: null);

        page.WaitForAssertion(
            () => page.FindComponents<EpisodeSurface>().ShouldHaveSingleItem(),
            Patience);
    }

    /// <summary>
    /// And an unrecognised one folds onto the same default rather than erroring or rendering an
    /// empty body — the same fold <c>ProjectRoute.Parse</c> applies for the sidebar and the tab
    /// strip, so a hand-typed URL agrees with the chrome drawn around it.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedTab_LandsOnEpisodesToo()
    {
        var project = await AddProjectAsync();

        var page = RenderAt(project.Id, "briefs");

        page.WaitForAssertion(
            () => page.FindComponents<EpisodeSurface>().ShouldHaveSingleItem(),
            Patience);
        page.FindComponents<InjectionLogTab>().ShouldBeEmpty();
    }

    /// <summary>
    /// An id that resolves to nothing — a deleted Project, or a pasted guid that never was one —
    /// draws the shared notice instead of a surface over no Project. The counterpart on the other
    /// page is <c>EpisodePageTests.AnUnresolvableId_DrawsTheSameNoticeTheOtherPageDoes</c>, which
    /// is where the two are held to the same words.
    /// </summary>
    [Fact]
    public void AnUnresolvableId_DrawsTheUnknownProjectNotice()
    {
        var page = RenderAt(Guid.CreateVersion7(), tab: null);

        page.WaitForAssertion(
            () => page.Find("div.page-notice h1").TextContent.ShouldBe("Unknown Project"),
            Patience);
        page.FindComponents<EpisodeSurface>().ShouldBeEmpty();
        page.FindComponents<InjectionLogTab>().ShouldBeEmpty();
    }
}
