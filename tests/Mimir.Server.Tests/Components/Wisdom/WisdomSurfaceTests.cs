using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Shared;
using Mimir.Server.Components.Wisdom;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Wisdom;

/// <summary>
/// The §8.1 surface, at the seams no pure test reaches. <c>WisdomBrowser</c> answers what the
/// queries return and <c>WisdomDisplay</c> decides every word and figure — both pinned already —
/// so what is left here is the wiring between them: which pane renders, what a selection resets,
/// what a Project switch sheds, and which of two ids an action takes.
/// <para>
/// Postgres tier throughout: the surface resolves <c>WisdomBrowser</c> and queries on every
/// parameter set. The waits are generous for the reason <c>.claude/rules/tests.md</c> gives — the
/// claim taken on mount schedules a debounced refresh on a real 250 ms timer, so what is being
/// waited on is usually the second query, not the first.
/// </para>
/// </summary>
public class WisdomSurfaceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private (BunitContext Render, SurfaceSearch Search) NewCircuit()
    {
        var render = CreateRenderContext();
        return (render, render.Services.GetRequiredService<SurfaceSearch>());
    }

    private static IRenderedComponent<WisdomSurface> RenderAt(
        BunitContext render, Guid projectId, Guid? selectedId = null,
        WisdomLens lens = WisdomLens.Active)
        => render.Render<WisdomSurface>(p => p
            .Add(c => c.ProjectId, projectId)
            .Add(c => c.SelectedId, selectedId)
            .Add(c => c.Lens, lens));

    private static void WaitForDetail(IRenderedComponent<WisdomSurface> surface)
        => surface.WaitForAssertion(() => surface.Find("p.detail-text"), Patience);

    /// <summary>
    /// The detail on screen and the surface done moving — see <see cref="RenderQuiescence"/> for
    /// why anything that clicks needs the second half.
    /// </summary>
    private static async Task ReadyForClicksAsync(IRenderedComponent<WisdomSurface> surface)
    {
        WaitForDetail(surface);
        await surface.SettleAsync();
    }

    private static void WaitForRows(IRenderedComponent<WisdomSurface> surface, int rows)
        => surface.WaitForAssertion(
            () => surface.FindAll("a.wisdom-row").Count.ShouldBe(rows), Patience);

    /// <summary>
    /// One screen on both routes (#91): the listing route renders the list with the placeholder
    /// beside it, and the detail route renders the same list with that Wisdom open. Asserted from
    /// one instance driven across the boundary, because "the list is neither torn down nor
    /// re-queried" is the whole reason both routes are one component.
    /// </summary>
    [Fact]
    public async Task SelectingARow_OpensTheDetailBesideTheSameList()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "prefer explicit over clever");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id);
        WaitForRows(surface, 1);
        surface.Find("p.detail-empty").TextContent.ShouldContain("Pick a Wisdom");

        surface.Render(p => p
            .Add(c => c.ProjectId, project.Id)
            .Add(c => c.SelectedId, wisdom.Id));

        WaitForDetail(surface);
        surface.Find("p.detail-text").TextContent.ShouldBe("prefer explicit over clever");
        surface.FindAll("a.wisdom-row").Count.ShouldBe(1);
    }

    /// <summary>
    /// A detail route naming a Wisdom that no longer exists says so and offers the way back,
    /// rather than rendering a blank pane the curator cannot tell from a slow query.
    /// </summary>
    [Fact]
    public async Task ADeadDeepLink_SaysSoAndOffersTheListBack()
    {
        var project = await AddProjectAsync();
        var (render, _) = NewCircuit();

        var surface = RenderAt(render, project.Id, selectedId: Guid.CreateVersion7());

        surface.WaitForAssertion(
            () => surface.Find("p.detail-empty").TextContent.ShouldContain("Nothing lives at this id"),
            Patience);
        surface.Find("p.detail-empty a").GetAttribute("href")
            .ShouldBe($"projects/{project.Id}/wisdom");
    }

    /// <summary>
    /// The list is the Project's ambient universe — its own Wisdom plus Global (ADR-0009) — so the
    /// head states the arithmetic rather than leaving a curator to infer why it is longer than the
    /// sidebar's Project-owned count.
    /// </summary>
    [Fact]
    public async Task TheListHead_StatesBothHalvesOfTheAmbientUniverse()
    {
        var project = await AddProjectAsync("mimir");
        await AddWisdomAsync(project.Id, "a project fact");
        await AddWisdomAsync(Project.GlobalId, "a global fact");
        var (render, _) = NewCircuit();

        var surface = RenderAt(render, project.Id);

        WaitForRows(surface, 2);
        var counts = surface.Find("span.list-counts").TextContent;
        counts.ShouldContain("1 · mimir");
        counts.ShouldContain("1 · Global");
    }

    /// <summary>
    /// Browsing Global itself has no second half to state — its universe is exactly its own rows,
    /// so stating "0 · Global + 1 · Global" would be arithmetic about nothing.
    /// </summary>
    [Fact]
    public async Task BrowsingGlobal_StatesOneFigureRatherThanASum()
    {
        await AddWisdomAsync(Project.GlobalId, "a global fact");
        var (render, _) = NewCircuit();

        var surface = RenderAt(render, Project.GlobalId);

        WaitForRows(surface, 1);
        surface.FindAll("span.counts-join").ShouldBeEmpty();
    }

    /// <summary>
    /// The heading and the empty-list sentence come from one switch, so a lens can never be
    /// titled one thing and explained as another. Driven over every lens the domain has, because
    /// the failure is a lens added to one arm and forgotten in the other.
    /// </summary>
    [Fact]
    public async Task EveryLens_TitlesItselfAndExplainsItsOwnEmptiness()
    {
        var project = await AddProjectAsync();
        var (render, _) = NewCircuit();

        foreach (var lens in Enum.GetValues<WisdomLens>())
        {
            var surface = RenderAt(render, project.Id, lens: lens);

            surface.WaitForAssertion(() => surface.Find("div.list-rows p.pane-note"), Patience);
            var heading = surface.Find("h4").TextContent;
            var empty = surface.Find("div.list-rows p.pane-note").TextContent;
            heading.ShouldNotBeNullOrWhiteSpace();
            empty.ShouldNotBeNullOrWhiteSpace();
            if (lens is not WisdomLens.Active)
            {
                empty.ShouldContain(heading, Case.Insensitive);
            }
        }
    }

    /// <summary>
    /// The link a row writes carries the Project whose universe is being read, never the row's own
    /// Scope — following a Global row out of <c>mimir</c>'s list must not switch the curator to
    /// Global's. The one site the whole family is pinned at
    /// (<c>.claude/rules/blazor-ui.md</c>).
    /// </summary>
    [Fact]
    public async Task ALinkToAGlobalRow_StaysInTheBrowsedProjectsUniverse()
    {
        var project = await AddProjectAsync();
        var global = await AddWisdomAsync(Project.GlobalId, "a global fact");
        var (render, _) = NewCircuit();

        var surface = RenderAt(render, project.Id);

        WaitForRows(surface, 1);
        surface.Find("a.wisdom-row").GetAttribute("href")
            .ShouldBe($"projects/{project.Id}/wisdom/{global.Id}");
    }

    /// <summary>
    /// A provenance link that has an Episode behind it drills to that Episode's page anchored on
    /// the Event, through <c>EpisodeDisplay.EventAnchorHref</c> — the same spelling the stream
    /// writes its ids from. A harvested file has no §8.2 surface, so its entry is a span: an
    /// anchor to nowhere is a worse answer than a row that does not offer to open.
    /// </summary>
    [Fact]
    public async Task ProvenanceOpensOnlyWhereThereIsSomethingToOpen()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        var evt = await AddEventAsync(episode.Id, seq: 1);
        await AddProvenanceAsync(wisdom.Id, episodeId: episode.Id, eventId: evt.Id);
        await AddHarvestProvenanceAsync(wisdom.Id, project.Id);
        var (render, _) = NewCircuit();

        var surface = RenderAt(render, project.Id, wisdom.Id);

        surface.WaitForAssertion(
            () => surface.FindAll("div.aside-links > *").Count.ShouldBe(2), Patience);
        surface.Find("a.aside-link").GetAttribute("href")
            .ShouldBe($"projects/{project.Id}/episodes/{episode.Id}{EpisodeDisplay.EventAnchorHref(evt.Id)}");
        surface.FindAll("span.aside-link").ShouldHaveSingleItem();
    }

    /// <summary>
    /// The aside and the cause legend are for reading a Wisdom, so neither renders over the
    /// unselected placeholder — an aside of blanks is three headings claiming nothing, and a
    /// glossary explains badges that are not on screen.
    /// </summary>
    [Fact]
    public async Task TheAsideAndTheLegend_RenderOnlyBesideAWisdom()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id);
        WaitForRows(surface, 1);

        surface.FindAll("aside.pane-aside").ShouldBeEmpty();
        surface.FindAll("footer.cause-legend").ShouldBeEmpty();

        surface.Render(p => p
            .Add(c => c.ProjectId, project.Id)
            .Add(c => c.SelectedId, wisdom.Id));

        surface.WaitForAssertion(
            () => surface.FindAll("aside.pane-aside").ShouldHaveSingleItem(), Patience);
        surface.FindAll("footer.cause-legend").ShouldHaveSingleItem();
    }

    /// <summary>
    /// The legend is one flowing paragraph, so each entry ends in a real space rather than a
    /// margin — two elements with only a gap between them give the renderer nowhere to break the
    /// line, and Razor trims the literal whitespace that would otherwise be there.
    /// </summary>
    [Fact]
    public async Task TheLegendEntries_AreSeparatedByRealSpaces()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var (render, _) = NewCircuit();

        var surface = RenderAt(render, project.Id, wisdom.Id);

        surface.WaitForAssertion(
            () => surface.Find("footer.cause-legend p").TextContent
                .ShouldContain(" — ", Case.Sensitive), Patience);
        var legend = surface.Find("footer.cause-legend p");
        foreach (var word in legend.QuerySelectorAll("b"))
        {
            word.NextSibling!.TextContent.ShouldEndWith(" ");
        }
    }

    /// <summary>
    /// The chips are a toggle group, so <c>aria-pressed</c> has to be there on both states — a
    /// bool would render it as a present-or-absent attribute, which is not what it means, and the
    /// unpressed chips would go silent. Every Kind gets a chip whether or not the universe holds
    /// one, so a chip row never reshuffles under the curator's cursor.
    /// </summary>
    [Fact]
    public async Task TheKindChips_ReportPressedOnBothStates()
    {
        var project = await AddProjectAsync();
        await AddWisdomAsync(project.Id, "a fact", kind: WisdomKind.Lesson);
        var (render, _) = NewCircuit();

        var surface = RenderAt(render, project.Id);

        var chips = 1 + Enum.GetValues<WisdomKind>().Length;
        surface.WaitForAssertion(
            () => surface.FindAll("button.chip").Count.ShouldBe(chips), Patience);
        surface.FindAll("button.chip").Select(c => c.GetAttribute("aria-pressed"))
            .ShouldBe(["true", .. Enumerable.Repeat("false", chips - 1)]);
    }

    /// <summary>
    /// Delete is absent entirely while the editor is open (§8.1), so "Delete forever" can never be
    /// the click that throws away a draft still being typed. Absent rather than disabled: a
    /// disabled danger button beside a textarea is still the thing the eye lands on.
    /// </summary>
    [Fact]
    public async Task OpeningTheEditor_TakesTheDeleteOffTheScreen()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id, wisdom.Id);
        await ReadyForClicksAsync(surface);
        surface.FindComponents<ConfirmDelete>().ShouldHaveSingleItem();

        await surface.ClickAsync("div.detail-actions button", "Edit");

        surface.Find("textarea.detail-editor").ShouldNotBeNull();
        surface.FindComponents<ConfirmDelete>().ShouldBeEmpty();
    }

    /// <summary>
    /// Selecting another row drops the outgoing one's half-typed draft. The armed-Delete half of
    /// the same rule is <c>ConfirmDelete</c>'s own since #106 and pinned there; what stays this
    /// surface's is the editor, which nothing else would close.
    /// </summary>
    [Fact]
    public async Task SelectingAnotherRow_ClosesTheEditorOnTheOutgoingOne()
    {
        var project = await AddProjectAsync();
        var first = await AddWisdomAsync(project.Id, "the first fact");
        var second = await AddWisdomAsync(project.Id, "the second fact");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id, first.Id);
        await ReadyForClicksAsync(surface);
        await surface.ClickAsync("div.detail-actions button", "Edit");
        await surface.InvokeAsync(() => surface.Find("textarea.detail-editor").Input("half a rewrite"));

        surface.Render(p => p
            .Add(c => c.ProjectId, project.Id)
            .Add(c => c.SelectedId, second.Id));

        surface.WaitForAssertion(
            () => surface.Find("p.detail-text").TextContent.ShouldBe("the second fact"), Patience);
        surface.FindAll("textarea.detail-editor").ShouldBeEmpty();
    }

    /// <summary>
    /// A draft that has parted from the stored text rides into the chain as the version it would
    /// become, so a curator reads their own rewording against what stands — and is told plainly it
    /// is not saved. Passed raw beside the same text Save reads, which is what keeps the pending
    /// row and the Save button from disagreeing about whether there is an edit at all.
    /// </summary>
    [Fact]
    public async Task TypingADraft_ShowsItAsThePendingVersionItWouldBecome()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id, wisdom.Id);
        await ReadyForClicksAsync(surface);
        await surface.ClickAsync("div.detail-actions button", "Edit");
        surface.FindAll("li.version-row.is-pending").ShouldBeEmpty();

        await surface.InvokeAsync(() => surface.Find("textarea.detail-editor").Input("a sharper fact"));

        var pending = surface.FindAll("li.version-row.is-pending").ShouldHaveSingleItem();
        pending.TextContent.ShouldContain("unsaved");
        pending.QuerySelector("span.version-seq")!.TextContent.ShouldBe("v2");
    }

    /// <summary>
    /// A draft that says exactly what the stored text already says is no version at all — the same
    /// answer the Merge Gate's no-op set gives, which is why the two are asked the same question
    /// rather than each deciding for itself.
    /// </summary>
    [Fact]
    public async Task ADraftThatChangesNothing_IsNeitherAPendingVersionNorSavable()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id, wisdom.Id);
        await ReadyForClicksAsync(surface);
        await surface.ClickAsync("div.detail-actions button", "Edit");

        await surface.InvokeAsync(() => surface.Find("textarea.detail-editor").Input("a fact"));

        surface.FindAll("li.version-row.is-pending").ShouldBeEmpty();
        surface.Find("button.btn-primary").HasAttribute("disabled").ShouldBeTrue();
    }

    /// <summary>
    /// The diff spells out each run's implicit role, because browsers largely do not expose bare
    /// <c>del</c> and <c>ins</c> to a screen reader — read straight through, the row becomes the
    /// old wording and the new one spoken as one sentence no version ever said.
    /// </summary>
    [Fact]
    public async Task TheDiffRuns_CarryExplicitRolesForScreenReaders()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id, wisdom.Id);
        await ReadyForClicksAsync(surface);
        await surface.ClickAsync("div.detail-actions button", "Edit");

        await surface.InvokeAsync(() => surface.Find("textarea.detail-editor").Input("a sharper fact"));

        var pending = surface.Find("li.version-row.is-pending");
        pending.QuerySelectorAll("del").ShouldAllBe(d => d.GetAttribute("role") == "deletion");
        pending.QuerySelectorAll("ins").ShouldAllBe(i => i.GetAttribute("role") == "insertion");
        pending.QuerySelectorAll("ins").ShouldNotBeEmpty();
    }

    /// <summary>
    /// How the chain reads is how this screen is being read right now, not what it is showing — so
    /// it outlives the selection (a curator working down a run of edits would otherwise re-pick it
    /// on every row) and it is not in the URL, where it would ride into every link the surface
    /// writes.
    /// </summary>
    [Fact]
    public async Task TheChainView_OutlivesTheSelectionAndStaysOutOfTheUrl()
    {
        var project = await AddProjectAsync();
        var first = await AddWisdomAsync(project.Id, "the first fact");
        var second = await AddWisdomAsync(project.Id, "the second fact");
        var (render, _) = NewCircuit();
        var surface = RenderAt(render, project.Id, first.Id);
        await ReadyForClicksAsync(surface);

        await surface.ClickAsync("button.chain-view", "Full text");

        surface.FindAll("p.version-text.is-full").ShouldNotBeEmpty();
        surface.Find("a.wisdom-row").GetAttribute("href").ShouldNotBeNull().ShouldNotContain("chain");

        surface.Render(p => p
            .Add(c => c.ProjectId, project.Id)
            .Add(c => c.SelectedId, second.Id));

        surface.WaitForAssertion(
            () => surface.Find("p.detail-text").TextContent.ShouldBe("the second fact"), Patience);
        surface.FindAll("p.version-text.is-full").ShouldNotBeEmpty();
    }

    /// <summary>
    /// Claimed from <c>OnInitialized</c>, where the parameters are already set — so the box is the
    /// Wisdom surface's before the first query returns, and the boundary's re-anchor has nothing
    /// to re-claim over.
    /// </summary>
    [Fact]
    public async Task Mounting_ClaimsTheSearchBoxForThisSurface()
    {
        var project = await AddProjectAsync();
        var (render, search) = NewCircuit();

        RenderAt(render, project.Id);

        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe("Search this Project's Wisdom…");
    }

    /// <summary>
    /// A Project switch sheds every narrowing made against the outgoing one — the typed term and
    /// the Kind chip both — because Blazor keeps this instance across the switch (#108). Released
    /// and re-claimed rather than merely released, or the incoming Project's list arrives
    /// unsearchable.
    /// </summary>
    [Fact]
    public async Task SwitchingProject_ShedsTheTermAndTheChipAndClaimsAgain()
    {
        var outgoing = await AddProjectAsync("outgoing");
        var incoming = await AddProjectAsync("incoming");
        await AddWisdomAsync(outgoing.Id, "an outgoing fact", kind: WisdomKind.Lesson);
        await AddWisdomAsync(incoming.Id, "an incoming fact", kind: WisdomKind.Procedure);
        var (render, search) = NewCircuit();
        var surface = RenderAt(render, outgoing.Id);
        WaitForRows(surface, 1);
        await surface.SettleAsync();
        // Not ClickAsync's exact-label overload: a chip's text is its Kind and its count run
        // together ("Lesson1"), so the match has to be on the prefix.
        await surface.InvokeAsync(() => surface.FindAll("button.chip")
            .First(c => c.TextContent.Trim().StartsWith("Lesson", StringComparison.Ordinal)).Click());
        search.Set("outgoing");

        surface.Render(p => p.Add(c => c.ProjectId, incoming.Id));

        search.Term.ShouldBe("");
        search.IsClaimed.ShouldBeTrue();
        surface.WaitForAssertion(
            () =>
            {
                surface.Find("button.chip.is-active").TextContent.Trim().ShouldBe("All");
                surface.Find("span.row-text").TextContent.ShouldBe("an incoming fact");
            },
            Patience);
    }

    /// <summary>
    /// Deleting the Wisdom the route names leaves the route pointing at a dead record, so the
    /// surface leaves for the listing. <c>ConfirmDelete</c> carries the id out (#106) and this
    /// takes that one rather than re-reading its own selection, which is the pair the window
    /// between a click and the read that replaces the detail can part.
    /// </summary>
    [Fact]
    public async Task DeletingTheSelectedWisdom_ReturnsToTheListing()
    {
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "a fact");
        var (render, _) = NewCircuit();
        var nav = render.Services.GetRequiredService<NavigationManager>();
        var surface = RenderAt(render, project.Id, wisdom.Id, WisdomLens.Retired);
        await ReadyForClicksAsync(surface);
        surface.FindComponents<ConfirmDelete>().ShouldHaveSingleItem();

        await surface.ClickAsync("div.pane-danger button");
        await surface.ClickAsync("div.pane-danger button.danger-fill");

        surface.WaitForAssertion(
            () => nav.Uri.ShouldEndWith($"projects/{project.Id}/wisdom?show=retired"), Patience);
        (await FromDb(db => db.Wisdom.CountAsync(_ => true, Token))).ShouldBe(0);
    }
}
