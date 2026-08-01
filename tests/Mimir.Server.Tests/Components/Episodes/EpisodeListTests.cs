using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Episodes;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Episodes;

public class EpisodeListTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private const string Placeholder = "Search this Project's Events…";

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
        var render = CreateRenderContext();

        var list = RenderAt(render, project.Id);

        list.WaitForAssertion(
            () => list.FindAll("a.episode-row").Count.ShouldBe(1), Patience);
    }

    [Fact]
    public async Task Mounting_ClaimsTheSearchBox()
    {
        var project = await AddProjectAsync();
        var render = CreateRenderContext(out SurfaceSearch search);

        RenderAt(render, project.Id);

        search.IsClaimed.ShouldBeTrue();
        search.Placeholder.ShouldBe(Placeholder);
    }

    [Fact]
    public void UnderGlobal_HoldsNoClaimAtAll()
    {
        var render = CreateRenderContext(out SurfaceSearch search);

        var list = RenderAt(render, Project.GlobalId, "Global", isGlobal: true);

        search.IsClaimed.ShouldBeFalse();
        list.Find("p.pane-note").TextContent.ShouldContain("Global holds Wisdom only");
    }

    [Fact]
    public async Task SwitchingProject_ShedsTheOutgoingProjectsTerm()
    {
        var outgoing = await AddProjectAsync("outgoing");
        var incoming = await AddProjectAsync("incoming");
        var render = CreateRenderContext(out SurfaceSearch search);
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
        var render = CreateRenderContext(out SurfaceSearch search);
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
        var render = CreateRenderContext(out SurfaceSearch search);
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
        var render = CreateRenderContext(out SurfaceSearch search);
        var list = RenderAt(render, project.Id);
        search.Set("migrations");

        list.Render(p => p.Add(c => c.SelectedId, episode.Id));

        search.Term.ShouldBe("migrations");
    }

    /// <summary>
    /// Every state a row can mark is a state the curator can filter to, so Running joins the drawn
    /// chips even though it is a queue state rather than a session one. Done is the resting
    /// majority — it is what "All" is mostly made of — so it marks nothing on its row and gets no
    /// chip: a chip for the default is a filter that changes nothing.
    /// </summary>
    [Fact]
    public async Task TheChips_CoverEveryStateARowCanMark_AndNothingElse()
    {
        var project = await AddProjectAsync();
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Done);
        var render = CreateRenderContext();

        var list = RenderAt(render, project.Id);

        // The chip's own word, read off its first text node: the count renders in a span right
        // beside it, so the whole chip's text reads "live0".
        list.WaitForAssertion(
            () => list.FindAll("button.chip").Select(c => c.FirstChild!.TextContent.Trim())
                .ShouldBe(["All", "live", "pending", "running", "failed"]),
            Patience);
        // The seeded Episode is Done, so it is in the list and marks nothing.
        list.FindAll("a.episode-row").Count.ShouldBe(1);
        list.FindAll("a.episode-row span.state-word, a.episode-row span.state-pill").ShouldBeEmpty();
    }

    /// <summary>
    /// Live and Failed are the two states a curator is scanning for, so they read as words in
    /// their own hue; the queue's own states are pills, so a row's distillation reads as a stage
    /// rather than an alarm. Both marks in one assertion, because the rule is the contrast.
    /// </summary>
    [Fact]
    public async Task AFailedRowReadsAsAWord_AndAQueuedOneAsAPill()
    {
        var project = await AddProjectAsync();
        await AddEpisodeAsync(
            project.Id, sealedAt: Now, distillation: DistillationState.Failed);
        await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddMinutes(-1), distillation: DistillationState.Pending);
        var render = CreateRenderContext();

        var list = RenderAt(render, project.Id);

        list.WaitForAssertion(
            () => list.FindAll("a.episode-row").Count.ShouldBe(2), Patience);
        list.Find("a.episode-row span.state-word").ClassList.ShouldContain("is-failed");
        list.Find("a.episode-row span.state-pill").TextContent.Trim().ShouldBe("pending");
    }

    /// <summary>
    /// The session id keys the Episode (ADR-0003) but it is a hash, so it rides as the row's
    /// tooltip rather than taking a line the curator has to read past on every row.
    /// </summary>
    [Fact]
    public async Task TheSessionId_RidesAsATooltipRatherThanARowLine()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        var render = CreateRenderContext();

        var list = RenderAt(render, project.Id);

        list.WaitForAssertion(
            () => list.FindAll("a.episode-row").Count.ShouldBe(1), Patience);
        var row = list.Find("a.episode-row");
        row.GetAttribute("title").ShouldBe($"session {episode.SessionId}");
        row.TextContent.ShouldNotContain(episode.SessionId);
    }

    /// <summary>
    /// An empty result under a term says <em>how the search reads</em> — whole words out of Event
    /// text — rather than claiming no Event mentions it. The two are different statements and only
    /// the first is true: the leg is FTS over the payloads, so a fragment, an unstemmed form or a
    /// stop-word finds nothing while the word is plainly there on screen a moment later. The
    /// Injection log's twin note is pinned the same way and for the same reason.
    /// </summary>
    [Fact]
    public async Task ASearchThatMatchesNothing_SaysHowTheSearchReads()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now);
        await AddEventAsync(episode.Id, seq: 1, payload: """{"prompt":"migrations"}""");
        var render = CreateRenderContext(out SurfaceSearch search);
        var list = RenderAt(render, project.Id);
        list.WaitForAssertion(() => list.FindAll("a.episode-row").Count.ShouldBe(1), Patience);

        // A mid-word fragment of a word that *is* in the payload — not a prefix, which the English
        // stemmer would fold onto the same lexeme and match. The note has to be true of this, and
        // "no Event mentions “igratio”" would not be.
        search.Set("igratio");

        // Whitespace collapsed the way a browser lays the note out: the sentence is wrapped across
        // source lines, so the phrase is contiguous on screen and nowhere in the raw text node.
        list.WaitForAssertion(
            () => string.Join(' ', list.Find("p.pane-note").TextContent
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ShouldContain("Search reads whole words out of Event text"),
            Patience);
    }

    [Fact]
    public async Task DisposingTheList_HandsTheBoxBack()
    {
        var project = await AddProjectAsync();
        var render = CreateRenderContext(out SurfaceSearch search);
        var list = RenderAt(render, project.Id);

        list.Instance.Dispose();

        search.IsClaimed.ShouldBeFalse();
    }
}
