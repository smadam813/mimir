using Microsoft.AspNetCore.Components;
using Mimir.Server.Components.Pages;

namespace Mimir.Server.Tests.Components.Pages;

/// <summary>
/// The chassis's route table, read off the pages themselves. Three rules meet here and none of
/// them is reachable by rendering a component: Blazor resolves a URL before any component exists,
/// so the renderer both tiers of <c>RenderTestBase</c>/<c>CreateRenderContext</c> give out is the
/// wrong instrument. What is ours rather than the framework's is which literal segments the pages
/// declare — the framework's own precedence (a literal outranks a parameter) does the rest — and
/// that is exactly what a route-attribute scan pins.
/// <para>
/// Disconnected: attributes, no database, no renderer.
/// </para>
/// </summary>
public class PageRoutesTests
{
    private static string[] RoutesOf<TPage>() where TPage : IComponent
        => [.. typeof(TPage)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(r => r.Template)];

    /// <summary>
    /// #95: one screen on both routes, so a curator working down the list is not torn down and
    /// re-queried on every click. Both templates on one page is what makes the component survive
    /// the selection — a separate drill-down page would remount it.
    /// </summary>
    [Fact]
    public void TheEpisodeScreen_ServesBothTheListingAndTheDrillDown()
        => RoutesOf<EpisodePage>().ShouldBe(
            [
                "/projects/{ProjectId:guid}/episodes",
                "/projects/{ProjectId:guid}/episodes/{EpisodeId:guid}",
            ],
            ignoreOrder: true);

    /// <summary>#91, the same shape one surface earlier, and what keeps every existing deep link resolving.</summary>
    [Fact]
    public void TheWisdomScreen_ServesBothTheListingAndTheDetail()
        => RoutesOf<WisdomPage>().ShouldBe(
            [
                "/projects/{ProjectId:guid}/wisdom",
                "/projects/{ProjectId:guid}/wisdom/{WisdomId:guid}",
            ],
            ignoreOrder: true);

    /// <summary>
    /// Why <c>ProjectPage</c>'s switch has no Wisdom or Episodes case to answer for: its
    /// <c>{Tab}</c> is a parameter segment, and the two surfaces above spell theirs out, so a
    /// literal beats it at every one of those URLs. Read as a set rather than per page, because
    /// the rule is a comparison between them: adding <c>/projects/{id}/wisdom</c> back to
    /// <c>ProjectPage</c>, or dropping it from <c>WisdomPage</c>, breaks it from either side.
    /// </summary>
    [Fact]
    public void TheTabRoute_IsAParameterEveryPortedSurfaceOutranks()
    {
        var tabbed = RoutesOf<ProjectPage>();

        tabbed.ShouldBe(["/projects/{ProjectId:guid}", "/projects/{ProjectId:guid}/{Tab}"], ignoreOrder: true);
        foreach (var literal in RoutesOf<WisdomPage>().Concat(RoutesOf<EpisodePage>()))
        {
            tabbed.ShouldNotContain(literal);
        }
    }
}
