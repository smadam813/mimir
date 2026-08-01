using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Components.Shared;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Components.Episodes;

/// <summary>
/// The §8.2 surface as a whole: which of its three detail branches renders, and what the panes
/// beside each other are reading from. <c>EpisodeBrowser</c> and <c>EpisodeDisplay</c> answer what
/// the rows say; what is here is the branching and the wiring — including the one thing the old
/// comment above the aside admitted no test held, that the aside renders from the branch that has
/// the detail rather than re-asking whether there is one.
/// </summary>
public class EpisodeSurfaceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private IRenderedComponent<EpisodeSurface> RenderAt(
        Guid projectId, Guid? selectedId = null, string name = "project", bool isGlobal = false)
        => CreateRenderContext().Render<EpisodeSurface>(p => p
            .Add(c => c.ProjectId, projectId)
            .Add(c => c.ProjectName, name)
            .Add(c => c.IsGlobal, isGlobal)
            .Add(c => c.SelectedId, selectedId));

    /// <summary>
    /// The listing route: the list is there and the detail pane says what a click would do, rather
    /// than sitting blank.
    /// </summary>
    [Fact]
    public async Task WithNothingSelected_TheDetailPaneInvitesAClick()
    {
        var project = await AddProjectAsync();
        await AddEpisodeAsync(project.Id, sealedAt: Now);

        var surface = RenderAt(project.Id);

        surface.WaitForAssertion(
            () => surface.Find("p.pane-placeholder").TextContent.ShouldContain("Pick a session"),
            Patience);
        surface.FindComponents<EpisodeList>().ShouldHaveSingleItem();
        surface.FindComponents<EpisodeDrillDown>().ShouldBeEmpty();
        surface.FindAll("aside.pane-aside").ShouldBeEmpty();
    }

    /// <summary>
    /// The drill-down route: the same list, plus the session's record and its own numbers. The
    /// aside comes from the branch that already holds the detail, so the two cannot disagree about
    /// whether there is one — asserted together for that reason.
    /// </summary>
    [Fact]
    public async Task SelectingASession_OpensTheRecordAndItsOwnNumbersTogether()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        await AddEventAsync(episode.Id, seq: 1);
        await AddEventAsync(episode.Id, seq: 2, type: EventType.PostToolUse);

        var surface = RenderAt(project.Id, episode.Id);

        surface.WaitForAssertion(
            () => surface.FindComponents<EpisodeDrillDown>().ShouldHaveSingleItem(), Patience);
        var aside = surface.Find("aside.pane-aside");
        aside.QuerySelector("span.aside-figure-value")!.TextContent.ShouldBe("2");
        // Whitespace collapsed the way a browser lays these out: the unit is four Razor
        // expressions on four lines, and what the curator reads is one phrase.
        var unit = string.Join(' ', aside.QuerySelector("span.aside-figure-unit")!
            .TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        unit.ShouldBe("Events, 1 prompt");
    }

    /// <summary>
    /// A drill-down id that answers nothing says the Episode was hard-deleted — but only once the
    /// query has come back for that id. A query still in flight and a deleted record both look
    /// like "no detail", and without telling them apart the note would flash on every click.
    /// </summary>
    [Fact]
    public async Task ADeletedEpisode_SaysSoRatherThanRenderingNothing()
    {
        var project = await AddProjectAsync();

        var surface = RenderAt(project.Id, Guid.CreateVersion7());

        surface.WaitForAssertion(
            () => surface.Find("p.pane-placeholder").TextContent
                .ShouldContain("This Episode does not exist"),
            Patience);
    }

    /// <summary>
    /// Deleting the Episode the route names leaves the route on a dead record, so the surface
    /// returns to the listing. The id comes from <c>ConfirmDelete</c> (#106), not from a re-read of
    /// the selection.
    /// </summary>
    [Fact]
    public async Task DeletingTheSelectedEpisode_ReturnsToTheListing()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        await AddEventAsync(episode.Id, seq: 1);
        var render = CreateRenderContext();
        var nav = render.Services.GetRequiredService<NavigationManager>();
        var surface = render.Render<EpisodeSurface>(p => p
            .Add(c => c.ProjectId, project.Id)
            .Add(c => c.ProjectName, "project")
            .Add(c => c.SelectedId, episode.Id));
        surface.WaitForAssertion(
            () => surface.Find("div.pane-danger button"), Patience);
        await surface.SettleAsync();

        await surface.ClickAsync("div.pane-danger button");
        await surface.ClickAsync("div.pane-danger button.danger-fill");

        surface.WaitForAssertion(
            () => nav.Uri.ShouldEndWith($"projects/{project.Id}/episodes"), Patience);
    }

    /// <summary>
    /// The whole-Episode delete is the one <c>ConfirmDelete</c> that is not <c>Subtle</c>: it sits
    /// in the danger zone apart from the per-Event deletes above it, because it takes the record
    /// entire. Both are drawn by the same component (§8.2), so a curator meets one confirmation
    /// shape everywhere.
    /// </summary>
    [Fact]
    public async Task EveryHardDeleteOnTheSurface_IsDrawnByTheOneConfirmation()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        await AddEventAsync(episode.Id, seq: 1);
        await AddEventAsync(episode.Id, seq: 2);

        var surface = RenderAt(project.Id, episode.Id);

        surface.WaitForAssertion(
            () => surface.FindComponents<ConfirmDelete>().Count.ShouldBe(3), Patience);
        var confirmations = surface.FindComponents<ConfirmDelete>();
        confirmations.Where(c => c.Instance.Subtle).Count().ShouldBe(2);
        surface.Find("div.pane-danger button").ClassList.ShouldContain("danger-outline");
    }
}
