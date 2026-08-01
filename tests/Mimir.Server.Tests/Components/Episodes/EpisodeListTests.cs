using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Episodes;

/// <summary>
/// #130's Postgres-tier flagship: the Episode list's claim on the header's search box, across the
/// boundary it is actually driven over. The rule has three parts and every one of them has been a
/// bug — the box is claimed from the parameter boundary rather than <c>OnInitialized</c>, released
/// and re-claimed when the Project changes, and not held at all under Global (#94, #108). Until
/// now it lived in prose (<c>.claude/rules/blazor-ui.md</c>) and nothing but a reader enforced it:
/// <see cref="SurfaceSearch"/> itself is pinned by <c>SurfaceSearchTests</c> and was never the
/// part that broke — what broke was a surface that stayed mounted across a switch and never told it.
/// <para>
/// On the Postgres tier because the list resolves <c>EpisodeBrowser</c> and queries on every
/// boundary; a term that failed to reset is only observable as the query it narrows.
/// </para>
/// </summary>
public class EpisodeListTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private const string Placeholder = "Search this Project's Events…";

    private (BunitContext Render, SurfaceSearch Search) NewCircuit()
    {
        var render = CreateRenderContext();
        return (render, render.Services.GetRequiredService<SurfaceSearch>());
    }

    /// <summary>The three parameters a sidebar row sets, mounting and switching alike.</summary>
    private static Action<ComponentParameterCollectionBuilder<EpisodeList>> At(
        Guid projectId, string name, bool isGlobal)
        => p => p
            .Add(c => c.ProjectId, projectId)
            .Add(c => c.ProjectName, name)
            .Add(c => c.IsGlobal, isGlobal);

    private static IRenderedComponent<EpisodeList> RenderAt(
        BunitContext render, Guid projectId, string name = "project", bool isGlobal = false)
        => render.Render(At(projectId, name, isGlobal));

    /// <summary>
    /// Blazor keeps one instance across a sidebar switch — same route, no <c>@key</c> — so the
    /// tests drive the switch the way the router does: new parameters onto the rendered component,
    /// never a fresh render. Same builder as the mount, for the same reason the router uses one
    /// set of parameters for both.
    /// </summary>
    private static void SwitchTo(
        IRenderedComponent<EpisodeList> list, Guid projectId, string name, bool isGlobal = false)
        => list.Render(At(projectId, name, isGlobal));

    /// <summary>
    /// The list queries what it was given, and the rows are the proof the harness reached Postgres
    /// at all — everything below reads a search term, which a list wired to nothing would report
    /// just as happily.
    /// </summary>
    [Fact]
    public async Task Mounting_ListsThisProjectsEpisodes()
    {
        var project = await AddProjectAsync();
        await AddEpisodeAsync(project.Id, sealedAt: Now);
        var (render, _) = NewCircuit();

        var list = RenderAt(render, project.Id);

        // The query is awaited in OnParametersSetAsync, so the first render is the empty one and
        // the rows arrive on a later one. Waited for generously rather than at bUnit's one-second
        // default: the claim taken a line earlier also schedules a debounced refresh, which
        // supersedes this one's generation, so what is actually being waited on is the *second*
        // query — and the first EF model build and Npgsql connect of a run land inside it.
        // Everything below reads the claim instead, taken synchronously ahead of that await.
        list.WaitForAssertion(
            () => list.FindAll("a.episode-row").Count.ShouldBe(1), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Claimed from the parameter boundary: the placeholder is the Episode list's, and it is there
    /// on the first render, before any keystroke or navigation.
    /// </summary>
    [Fact]
    public async Task Mounting_ClaimsTheSearchBox()
    {
        var project = await AddProjectAsync();
        var (render, search) = NewCircuit();

        RenderAt(render, project.Id);

        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe(Placeholder);
    }

    /// <summary>
    /// Global holds Wisdom only (§3), so there are no Episodes to narrow and the box is handed
    /// back rather than offered over nothing. The claim is by holder identity, so "holds no claim"
    /// is the assertion — an unclaimed box renders disabled with an explanation.
    /// <para>
    /// Mounting straight into Global, which is a different path from switching into it: there is no
    /// prior claim to release. Nothing is seeded and nothing needs to be — the §3 pseudo-project is
    /// the migration seed the harness restores on every reset, and the Global branch short-circuits
    /// ahead of the query, which is the behaviour under test rather than a gap in the fixture.
    /// </para>
    /// </summary>
    [Fact]
    public void UnderGlobal_HoldsNoClaimAtAll()
    {
        var (render, search) = NewCircuit();

        var list = RenderAt(render, Project.GlobalId, "Global", isGlobal: true);

        search.IsClaimed.ShouldBeFalse();
        list.Find("p.pane-note").TextContent.ShouldContain("Global holds Wisdom only");
    }

    /// <summary>
    /// The #94 bug, whole: the surface survives a sidebar switch, so a term typed for the outgoing
    /// Project would silently narrow the incoming one — a list that looks short for reasons nobody
    /// can see, since the box it came from is per claim and starts empty on both edges.
    /// </summary>
    [Fact]
    public async Task SwitchingProject_ShedsTheOutgoingProjectsTerm()
    {
        var outgoing = await AddProjectAsync("outgoing");
        var incoming = await AddProjectAsync("incoming");
        var (render, search) = NewCircuit();
        var list = RenderAt(render, outgoing.Id, "outgoing");
        search.Set("migrations");

        SwitchTo(list, incoming.Id, "incoming");

        search.Term.ShouldBe("");
        // Released *and* re-claimed: shedding the term by simply letting go would leave the
        // incoming Project's list unsearchable and the box disabled.
        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe(Placeholder);
    }

    /// <summary>
    /// Crossing to Global is the same boundary the other way: the term goes and so does the claim,
    /// rather than the tab arriving with the previous Project's narrowing still applied.
    /// </summary>
    [Fact]
    public async Task SwitchingToGlobal_ReleasesTheClaim()
    {
        var project = await AddProjectAsync();
        var (render, search) = NewCircuit();
        var list = RenderAt(render, project.Id);
        search.Set("migrations");

        SwitchTo(list, Project.GlobalId, "Global", isGlobal: true);

        search.IsClaimed.ShouldBeFalse();
        search.Term.ShouldBe("");
    }

    /// <summary>
    /// And back: a Project after Global claims again, so the box does not stay disabled for the
    /// rest of the circuit.
    /// </summary>
    [Fact]
    public async Task ReturningFromGlobal_ClaimsAgain()
    {
        var project = await AddProjectAsync();
        var (render, search) = NewCircuit();
        var list = RenderAt(render, Project.GlobalId, "Global", isGlobal: true);

        SwitchTo(list, project.Id, "project");

        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe(Placeholder);
    }

    /// <summary>
    /// The other edge of the same rule, and the one an unconditional release-and-re-claim would
    /// break: selecting a row is a route change on this surface (#95), so the list re-renders with
    /// nothing about it changed. Dropping the term there would empty the box under a curator who
    /// searched, then clicked a result.
    /// </summary>
    [Fact]
    public async Task SelectingARow_KeepsTheTerm()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        var (render, search) = NewCircuit();
        var list = RenderAt(render, project.Id);
        search.Set("migrations");

        list.Render(p => p.Add(c => c.SelectedId, episode.Id));

        search.Term.ShouldBe("migrations");
    }

    /// <summary>
    /// Two surfaces do not fight over one box: the outgoing list releases on dispose, and because
    /// the box is held by identity, a release that arrives after another holder has claimed does
    /// nothing (<see cref="SurfaceSearch"/>). Navigating away and back is the ordinary case Blazor
    /// produces — the incoming component mounts before the outgoing one is disposed.
    /// </summary>
    [Fact]
    public async Task DisposingTheList_HandsTheBoxBack()
    {
        var project = await AddProjectAsync();
        var (render, search) = NewCircuit();
        var list = RenderAt(render, project.Id);

        // What Blazor calls when the circuit navigates away; the release lives in Dispose.
        list.Instance.Dispose();

        search.IsClaimed.ShouldBeFalse();
    }
}
