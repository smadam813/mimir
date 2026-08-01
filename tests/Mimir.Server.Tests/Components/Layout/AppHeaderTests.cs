using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Components.Health;
using Mimir.Server.Components.Layout;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Components.Layout;

/// <summary>
/// The header answers whole-install, and the two things it has to get right are both about
/// <em>not</em> doing work: the pipeline is five Postgres queries, so it is never fetched before
/// first run is known false — null is a real third state here, not a falsy second — and the search
/// box is somebody else's state, so the header re-reads it rather than holding a copy.
/// <para>
/// Postgres tier: the pipeline is a query, and "was it fetched" is only observable as figures that
/// did or did not arrive.
/// </para>
/// </summary>
public class AppHeaderTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>
    /// The cascade <c>MainLayout</c> supplies, in whichever of its three states. Wrapped in a real
    /// <see cref="CascadingValue{TValue}"/> through the render tree rather than bUnit's
    /// <c>AddCascadingValue</c>, which constrains its value to <c>notnull</c> — and null is the
    /// state half of this class is about.
    /// </summary>
    private static IRenderedComponent<AppHeader> RenderUnder(BunitContext render, bool? isFirstRun)
    {
        render.RenderTree.Add<CascadingValue<bool?>>(p => p
            .Add(c => c.Name, MainLayout.FirstRunCascade)
            .Add(c => c.Value, isFirstRun));
        return render.Render<AppHeader>();
    }

    /// <summary>
    /// Null is "no answer yet", and the header spends it doing nothing: no pipeline, because five
    /// queries for figures that may be replaced a render later is the waste this shape exists to
    /// avoid, and no pull chip either, because that would claim a first run nobody has confirmed.
    /// Waited past the point a real fetch would have landed, so this is not just reading the first
    /// render before an await.
    /// </summary>
    [Fact]
    public async Task WhileFirstRunIsUnknown_NeitherTheFigureNorTheChipIsAskedFor()
    {
        await AddProjectAsync();
        var render = CreateRenderContext();

        var header = RenderUnder(render, isFirstRun: null);
        // Silence for the same span a positive pin waits a query to arrive in, rather than the
        // default window: the claim is that a query did not run, and a query in flight renders
        // nothing, so a shorter wait would read the screen before the regression could reach it.
        await header.SettleAsync(Patience);

        header.FindAll("div.pipeline").ShouldBeEmpty();
        header.FindComponents<ModelPullChip>().ShouldBeEmpty();
    }

    /// <summary>
    /// The answer landing false is also the header's initial fetch — the cascade change is the
    /// first moment the pipeline is worth asking for, and nothing else asks afterwards until the
    /// feed does.
    /// </summary>
    [Fact]
    public async Task OnceFirstRunIsKnownFalse_ThePipelineIsFetchedAndDrawn()
    {
        var project = await AddProjectAsync();
        await AddEpisodeAsync(project.Id, sealedAt: Now);
        var render = CreateRenderContext();

        var header = RenderUnder(render, isFirstRun: false);

        header.WaitForAssertion(
            () => header.Find("div.pipeline").TextContent.ShouldContain("Episodes"),
            TimeSpan.FromSeconds(10));
        header.FindComponents<ModelPullChip>().ShouldBeEmpty();
    }

    /// <summary>
    /// And true swaps the two rather than showing both: four zeros say nothing an empty install
    /// does not already know, and the header's 1380px floor does not fit the pair anyway.
    /// </summary>
    [Fact]
    public void OnFirstRun_ThePullChipTakesThePipelinesPlace()
    {
        var render = CreateRenderContext();

        var header = RenderUnder(render, isFirstRun: true);

        header.FindComponents<ModelPullChip>().ShouldHaveSingleItem();
        header.FindAll("div.pipeline").ShouldBeEmpty();
    }

    /// <summary>
    /// The one box narrows whichever surface is on screen, so with nothing on screen to narrow it
    /// says so rather than pretending to search. Disabled *and* explained: a greyed box with no
    /// reason reads as broken.
    /// </summary>
    [Fact]
    public void WithNoSurfaceHoldingTheClaim_TheBoxIsDisabledAndSaysWhy()
    {
        var render = CreateRenderContext();

        var box = RenderUnder(render, isFirstRun: false).Find("input.input");

        box.HasAttribute("disabled").ShouldBeTrue();
        box.GetAttribute("title").ShouldNotBeNull().ShouldContain("Nothing here claims search");
    }

    /// <summary>
    /// A claim taken by a surface changes this input's placeholder and enables it, and none of
    /// that is the header's own state — so the header re-renders on <c>Changed</c> and re-reads the
    /// service. Driven through <see cref="SurfaceSearch"/> the way a mounting surface would, rather
    /// than by re-rendering the header, because "the header noticed" is the assertion.
    /// </summary>
    [Fact]
    public async Task WhenASurfaceClaimsTheBox_TheHeaderReRendersAroundIt()
    {
        var render = CreateRenderContext(out SurfaceSearch search);
        var header = RenderUnder(render, isFirstRun: false);
        // Settled first, or the pipeline query's own StateHasChanged lands after the claim and
        // repaints the box for reasons that have nothing to do with the subscription under test.
        await header.SettleAsync();

        using var claim = search.Claim(this, "Search this Project's Events…");
        await header.SettleAsync();

        // Asserted after settling rather than through WaitForAssertion, because the regression
        // this is against — the header not subscribing at all — produces *no* further render, and
        // a wait that never sees one is not a claim about the markup.
        var box = header.Find("input.input");
        box.HasAttribute("disabled").ShouldBeFalse();
        box.GetAttribute("placeholder").ShouldBe("Search this Project's Events…");
    }

    /// <summary>
    /// And the release is the same signal: the box empties and goes back to saying it has nothing
    /// to narrow, so a term typed against the outgoing surface is not left sitting in the chrome.
    /// </summary>
    [Fact]
    public async Task WhenTheClaimIsHandedBack_TheBoxEmptiesAndDisablesAgain()
    {
        var render = CreateRenderContext(out SurfaceSearch search);
        var header = RenderUnder(render, isFirstRun: false);
        await header.SettleAsync();
        var claim = search.Claim(this, "Search this Project's Events…");
        search.Set("migrations");
        await header.SettleAsync();
        // The box has to reach the claimed state first, or "disabled and empty" below is just the
        // state it started in and the assertion proves nothing.
        var claimed = header.Find("input.input");
        claimed.HasAttribute("disabled").ShouldBeFalse();
        claimed.GetAttribute("value").ShouldBe("migrations");

        claim.Dispose();
        await header.SettleAsync();

        var box = header.Find("input.input");
        box.HasAttribute("disabled").ShouldBeTrue();
        box.GetAttribute("value").ShouldBeNullOrEmpty();
    }

    /// <summary>
    /// Typing goes to the service, not to a field of the header's own — which is what lets the
    /// claiming surface read the term back without the two ever holding different strings.
    /// </summary>
    [Fact]
    public async Task TypingInTheBox_SetsTheTermOnTheService()
    {
        var render = CreateRenderContext(out SurfaceSearch search);
        var header = RenderUnder(render, isFirstRun: false);
        using var claim = search.Claim(this, "Search…");
        await header.SettleAsync();
        header.Find("input.input").HasAttribute("disabled").ShouldBeFalse();

        await header.InvokeAsync(() => header.Find("input.input").Input("migrations"));

        search.Term.ShouldBe("migrations");
    }
}
