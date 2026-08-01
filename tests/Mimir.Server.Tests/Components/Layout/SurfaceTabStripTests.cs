using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Layout;

namespace Mimir.Server.Tests.Components.Layout;

/// <summary>
/// The three-tab strip, which sits in the layout above <c>@Body</c> and takes no cascading
/// parameter from the page it is drawn over — it reads the Project and the active tab off the URL,
/// the same trick the sidebar uses. That is the whole of its interface, so the URL is what these
/// drive.
/// <para>
/// Postgres tier: the counts beside each tab are a query.
/// </para>
/// </summary>
public class SurfaceTabStripTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private IRenderedComponent<SurfaceTabStrip> RenderAt(string relativePath)
    {
        var render = CreateRenderContext();
        render.Services.GetRequiredService<NavigationManager>().NavigateTo(relativePath);
        return render.Render<SurfaceTabStrip>();
    }

    /// <summary>
    /// Off the <c>projects/{id}</c> route the strip draws nothing at all — there is no Project to
    /// count for, and an empty strip would leave the home page wearing a row of zeros for a
    /// Project nobody picked.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Error")]
    [InlineData("projects/not-a-guid/wisdom")]
    public void OffTheProjectRoute_TheStripDrawsNothing(string relativePath)
        => RenderAt(relativePath).Markup.Trim().ShouldBeEmpty();

    /// <summary>
    /// The three surfaces in spec order, each carrying this Project's own figure — a different
    /// question from the header's whole-install pipeline, which is why both exist.
    /// </summary>
    [Fact]
    public async Task OnAProjectRoute_TheThreeSurfacesCarryThatProjectsCounts()
    {
        var project = await AddProjectAsync();
        await AddWisdomAsync(project.Id, "a fact");
        await AddWisdomAsync(project.Id, "another fact");
        await AddEpisodeAsync(project.Id, sealedAt: Now);

        var strip = RenderAt($"projects/{project.Id}/wisdom");

        strip.WaitForAssertion(
            () => strip.FindAll("a.tab").Select(t => t.TextContent.Trim())
                .ShouldBe(["Wisdom2", "Episodes1", "Injections0"]),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The active tab is marked for the eye and for a screen reader both, and it is the URL's
    /// answer — including the fold a missing or unrecognised tab takes onto Episodes, so the strip
    /// never renders with nothing current.
    /// </summary>
    [Theory]
    [InlineData("wisdom", "Wisdom")]
    [InlineData("injections", "Injections")]
    [InlineData("episodes", "Episodes")]
    [InlineData("", "Episodes")]
    [InlineData("briefs", "Episodes")]
    public async Task TheCurrentTab_IsTheOneTheUrlNamesAfterTheFold(string tab, string expected)
    {
        var project = await AddProjectAsync();
        var suffix = tab.Length == 0 ? "" : $"/{tab}";

        var strip = RenderAt($"projects/{project.Id}{suffix}");

        strip.WaitForAssertion(
            () =>
            {
                var current = strip.FindAll("a.tab.is-active").ShouldHaveSingleItem();
                current.TextContent.Trim().ShouldStartWith(expected);
                current.GetAttribute("aria-current").ShouldBe("page");
            },
            TimeSpan.FromSeconds(10));
    }
}
