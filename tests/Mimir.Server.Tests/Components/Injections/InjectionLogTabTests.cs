using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Injections;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Injections;

/// <summary>
/// The §8.3 log. <c>InjectionBrowser</c> answers the figures and <c>InjectionDisplay</c> the
/// words; what is here is the four-pane shape, the two counts that answer different questions, and
/// the boundary behaviour a Project switch has to get right (#108).
/// </summary>
public class InjectionLogTabTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private (BunitContext Render, SurfaceSearch Search) NewCircuit()
    {
        var render = CreateRenderContext();
        return (render, render.Services.GetRequiredService<SurfaceSearch>());
    }

    private static IRenderedComponent<InjectionLogTab> RenderAt(BunitContext render, Guid projectId)
        => render.Render<InjectionLogTab>(p => p.Add(c => c.ProjectId, projectId));

    private static void WaitForRows(IRenderedComponent<InjectionLogTab> tab, int rows)
        => tab.WaitForAssertion(() => tab.FindAll("button.entry-row").Count.ShouldBe(rows), Patience);

    /// <summary>
    /// The shape §8.3 asks for: the session-grouped list, a detail pane that says what a click
    /// would give you, and this Project's own figures down the side.
    /// </summary>
    [Fact]
    public async Task TheSurface_IsTheListTheDetailAndTheProjectsFigures()
    {
        var project = await AddProjectAsync();
        await AddInjectionAsync(project.Id, sessionId: "sess-one");
        var (render, _) = NewCircuit();

        var tab = RenderAt(render, project.Id);

        WaitForRows(tab, 1);
        tab.Find("div.session-head span.session-id").TextContent.ShouldBe("sess-one");
        tab.Find("p.pane-placeholder").TextContent.ShouldContain("Pick an entry");
        tab.Find("aside.pane-aside").TextContent.ShouldContain("This Project");
    }

    /// <summary>
    /// Two counts answering two questions: the head states what the query matched — it sits above
    /// the chips and the list it describes — while the aside carries the whole-Project figure. A
    /// filtered listing that reported the Project's total would be the head lying about the list
    /// underneath it.
    /// </summary>
    [Fact]
    public async Task TheHeadCountsWhatMatched_AndTheAsideTheWholeProject()
    {
        var project = await AddProjectAsync();
        await AddInjectionAsync(project.Id, lane: InjectionLane.Prompt);
        await AddInjectionAsync(project.Id, lane: InjectionLane.Brief, queryContext: null);
        await AddInjectionAsync(project.Id, lane: InjectionLane.Brief, queryContext: null);
        var (render, _) = NewCircuit();
        var tab = RenderAt(render, project.Id);
        WaitForRows(tab, 3);
        await tab.SettleAsync();

        await tab.InvokeAsync(() => tab.FindAll("button.chip")
            .Single(c => c.TextContent.Contains("Brief", StringComparison.Ordinal)).Click());

        tab.WaitForAssertion(
            () => tab.Find("span.pane-count").TextContent.Trim().ShouldStartWith("2"), Patience);
        tab.Find("aside.pane-aside span.aside-figure-value").TextContent.ShouldBe("3");
    }

    /// <summary>
    /// Selecting an entry opens the detail beside the list rather than replacing it — the log is
    /// judged by comparing entries, so losing the list on every click would be the wrong screen.
    /// </summary>
    [Fact]
    public async Task SelectingAnEntry_OpensTheDetailBesideTheList()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        await AddInjectionAsync(project.Id, items: [(wisdom.Id, 0.9)]);
        var (render, _) = NewCircuit();
        var tab = RenderAt(render, project.Id);
        WaitForRows(tab, 1);
        await tab.SettleAsync();

        await tab.ClickAsync("button.entry-row");

        tab.FindComponents<InjectionDetail>().ShouldHaveSingleItem();
        tab.FindAll("button.entry-row").Count.ShouldBe(1);
        tab.Find("button.entry-row").ClassList.ShouldContain("is-selected");
    }

    /// <summary>
    /// The selection is re-resolved by id after every refresh, because the entries are records read
    /// fresh each time — without it the detail pane keeps rendering the record it was handed before
    /// the mark, and the buttons go on saying nothing is marked. Asserted on the *detail*, not on
    /// the list row: the row is redrawn from the fresh listing either way, so it stays right for
    /// reasons that have nothing to do with the selection.
    /// </summary>
    [Fact]
    public async Task MarkingAnEntry_ShowsTheNewVerdictOnTheSameSelection()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        await AddInjectionAsync(project.Id, items: [(wisdom.Id, 0.9)]);
        var (render, _) = NewCircuit();
        var tab = RenderAt(render, project.Id);
        WaitForRows(tab, 1);
        await tab.SettleAsync();
        await tab.ClickAsync("button.entry-row");

        await tab.InvokeAsync(() => tab.FindAll("button")
            .First(b => b.TextContent.Contains("Useful", StringComparison.OrdinalIgnoreCase)).Click());

        tab.WaitForAssertion(
            () =>
            {
                tab.Find("span.entry-mark").TextContent.Trim().ShouldBe("useful");
                tab.FindComponents<InjectionDetail>().ShouldHaveSingleItem()
                    .FindAll("button.btn-primary")
                    .Select(b => b.TextContent.Trim()).ShouldContain("Useful");
            },
            Patience);
        tab.Find("button.entry-row").ClassList.ShouldContain("is-selected");
    }

    /// <summary>
    /// A Project switch is a different log: the lane chip was chosen against the outgoing Project's
    /// counts and the selected entry is not in the incoming one at all. The router reuses this
    /// instance across the switch, so this is the only place either can be shed.
    /// </summary>
    [Fact]
    public async Task SwitchingProject_ShedsTheLaneChipAndTheSelection()
    {
        var outgoing = await AddProjectAsync("outgoing");
        var incoming = await AddProjectAsync("incoming");
        await AddInjectionAsync(outgoing.Id, lane: InjectionLane.Brief, queryContext: null);
        await AddInjectionAsync(incoming.Id, sessionId: "sess-incoming");
        var (render, _) = NewCircuit();
        var tab = RenderAt(render, outgoing.Id);
        WaitForRows(tab, 1);
        await tab.SettleAsync();
        await tab.ClickAsync("button.entry-row");
        await tab.InvokeAsync(() => tab.FindAll("button.chip")
            .Single(c => c.TextContent.Contains("Brief", StringComparison.Ordinal)).Click());

        tab.Render(p => p.Add(c => c.ProjectId, incoming.Id));

        tab.WaitForAssertion(
            () =>
            {
                tab.Find("button.chip.is-active").TextContent.Trim().ShouldBe("All");
                tab.Find("div.session-head span.session-id").TextContent.ShouldBe("sess-incoming");
                tab.Find("p.pane-placeholder").ShouldNotBeNull();
            },
            Patience);
    }

    /// <summary>
    /// And it sheds the typed term with it: the tab releases and re-claims the header's box, since
    /// a claim starts empty on both edges and nothing else resets the term while one instance lives
    /// (#108). Released first, or disposing the stale token would hand the box back from under the
    /// new claim.
    /// </summary>
    [Fact]
    public async Task SwitchingProject_ShedsTheTermAndKeepsTheClaim()
    {
        var outgoing = await AddProjectAsync("outgoing");
        var incoming = await AddProjectAsync("incoming");
        var (render, search) = NewCircuit();
        var tab = RenderAt(render, outgoing.Id);
        search.IsClaimed.ShouldBeTrue();
        search.Set("a prompt");

        tab.Render(p => p.Add(c => c.ProjectId, incoming.Id));

        search.Term.ShouldBe("");
        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe("Search this Project's injections…");
    }

    /// <summary>
    /// Search reads an entry's query text, and a Brief never has one (§3) — so an empty result
    /// under a term says how the search reads rather than leaving the curator to conclude the
    /// Project has no injections at all.
    /// </summary>
    [Fact]
    public async Task ASearchThatMatchesNothing_SaysWhatTheSearchReads()
    {
        var project = await AddProjectAsync();
        await AddInjectionAsync(project.Id, queryContext: "a prompt");
        var (render, search) = NewCircuit();
        var tab = RenderAt(render, project.Id);
        WaitForRows(tab, 1);

        search.Set("nothing matches this");

        tab.WaitForAssertion(
            () => tab.Find("p.pane-empty").TextContent.ShouldContain("Search reads an entry's query text"),
            Patience);
    }
}
