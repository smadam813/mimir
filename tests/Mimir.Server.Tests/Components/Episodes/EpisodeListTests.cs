using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Episodes;

public class EpisodeListTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private const string Placeholder = "Search this Project's Events…";

    private (BunitContext Render, SurfaceSearch Search) NewCircuit()
    {
        var render = CreateRenderContext();
        return (render, render.Services.GetRequiredService<SurfaceSearch>());
    }

    private static Action<ComponentParameterCollectionBuilder<EpisodeList>> At(
        Guid projectId, string name, bool isGlobal)
        => p => p
            .Add(c => c.ProjectId, projectId)
            .Add(c => c.ProjectName, name)
            .Add(c => c.IsGlobal, isGlobal);

    private static IRenderedComponent<EpisodeList> RenderAt(
        BunitContext render, Guid projectId, string name = "project", bool isGlobal = false)
        => render.Render(At(projectId, name, isGlobal));

    private static void SwitchTo(
        IRenderedComponent<EpisodeList> list, Guid projectId, string name, bool isGlobal = false)
        => list.Render(At(projectId, name, isGlobal));

    [Fact]
    public async Task Mounting_ListsThisProjectsEpisodes()
    {
        var project = await AddProjectAsync();
        await AddEpisodeAsync(project.Id, sealedAt: Now);
        var (render, _) = NewCircuit();

        var list = RenderAt(render, project.Id);

        list.WaitForAssertion(
            () => list.FindAll("a.episode-row").Count.ShouldBe(1), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Mounting_ClaimsTheSearchBox()
    {
        var project = await AddProjectAsync();
        var (render, search) = NewCircuit();

        RenderAt(render, project.Id);

        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe(Placeholder);
    }

    [Fact]
    public void UnderGlobal_HoldsNoClaimAtAll()
    {
        var (render, search) = NewCircuit();

        var list = RenderAt(render, Project.GlobalId, "Global", isGlobal: true);

        search.IsClaimed.ShouldBeFalse();
        list.Find("p.pane-note").TextContent.ShouldContain("Global holds Wisdom only");
    }

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
        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe(Placeholder);
    }

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

    [Fact]
    public async Task DisposingTheList_HandsTheBoxBack()
    {
        var project = await AddProjectAsync();
        var (render, search) = NewCircuit();
        var list = RenderAt(render, project.Id);

        list.Instance.Dispose();

        search.IsClaimed.ShouldBeFalse();
    }
}
