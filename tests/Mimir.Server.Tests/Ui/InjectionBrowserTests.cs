using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

public sealed class InjectionBrowserTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private const int MostRecalledSpread = InjectionBrowser.MostRecalledLimit + 1;

    [Fact]
    public async Task TheListing_GroupsPerSessionNewestFirst_AndCarriesTheEntrysShape()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var early = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now.AddMinutes(-10),
            items: [(wisdom.Id, 0.02)]);
        var late = await AddInjectionAsync(
            project.Id, "sess-b", InjectionLane.Prompt, "how do I deploy?", Now,
            items: [(wisdom.Id, 0.03)]);

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.Sessions.Select(s => s.SessionId).ShouldBe(["sess-b", "sess-a"]);
        var entry = view.Sessions[0].Entries.ShouldHaveSingleItem();
        entry.Id.ShouldBe(late.Id);
        entry.Lane.ShouldBe(InjectionLane.Prompt);
        entry.QueryContext.ShouldBe("how do I deploy?");
        entry.Chars.ShouldBe(late.Chars);
        entry.CanPromote.ShouldBeTrue();
        view.Sessions[1].Entries.ShouldHaveSingleItem().Id.ShouldBe(early.Id);
        view.Sessions[1].Entries[0].CanPromote.ShouldBeFalse();
    }

    [Fact]
    public async Task TheListing_IsScopedToTheProject()
    {
        var (project, other) = (await AddProjectAsync("injection"), await AddProjectAsync("injection"));
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        await AddInjectionAsync(
            other.Id, "sess-other", InjectionLane.Prompt, "elsewhere", Now,
            items: [(wisdom.Id, 0.03)]);

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Items_ArriveInStoredOrder_AsTheSameCardEntriesTheBrowserRenders()
    {
        var project = await AddProjectAsync("injection");
        var first = await AddWisdomAsync(project.Id, "the stronger match");
        var second = await AddWisdomAsync(project.Id, "the weaker match");
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(first.Id, 0.03), (second.Id, 0.02)]);

        var entry = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();

        entry.Items.Select(i => i.WisdomId).ShouldBe([first.Id, second.Id]);
        entry.Items[0].Score.ShouldBe(0.03);
        var card = entry.Items[0].Wisdom.ShouldNotBeNull();
        card.Text.ShouldBe("the stronger match");
        card.ScopeProjectId.ShouldBe(project.Id);
    }

    [Fact]
    public async Task AHardDeletedWisdom_LeavesItsItemVisible_WithoutACard()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);
        await Context.Wisdom.Where(w => w.Id == wisdom.Id).ExecuteDeleteAsync(Token);

        var entry = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();

        var item = entry.Items.ShouldHaveSingleItem();
        item.WisdomId.ShouldBe(wisdom.Id);
        item.Wisdom.ShouldBeNull();
    }

    [Fact]
    public async Task Marking_SticksWithVerdictAt_AndRemarkingSwitches()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);

        await Browser().MarkAsync(injection.Id, InjectionVerdict.Useful, Token);

        var marked = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.Single().Entries.Single();
        marked.Verdict.ShouldBe(InjectionVerdict.Useful);
        marked.VerdictAt.ShouldBe(Now);

        Clock.Advance(TimeSpan.FromMinutes(5));
        await Browser().MarkAsync(injection.Id, InjectionVerdict.Noise, Token);

        var remarked = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.Single().Entries.Single();
        remarked.Verdict.ShouldBe(InjectionVerdict.Noise);
        remarked.VerdictAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public async Task Precision_IsUsefulOverMarked_UnmarkedEntriesStayOut()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var items = new[] { (wisdom.Id, 0.03) };
        var useful1 = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "p1", Now, items: items);
        var useful2 = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "p2", Now, items: items);
        var noise = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "p3", Now, items: items);
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, null, Now, items: items);

        await Browser().MarkAsync(useful1.Id, InjectionVerdict.Useful, Token);
        await Browser().MarkAsync(useful2.Id, InjectionVerdict.Useful, Token);
        await Browser().MarkAsync(noise.Id, InjectionVerdict.Noise, Token);

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.Useful.ShouldBe(2);
        aside.Marked.ShouldBe(3);
        aside.Precision.ShouldNotBeNull().ShouldBe(2.0 / 3.0, tolerance: 1e-9);
    }

    /// <summary>
    /// A Project with nothing logged is also the case the folded count query has no group to read:
    /// <c>GROUP BY</c> over no rows returns no rows at all, so every figure here comes from the
    /// fallback rather than from Postgres.
    /// </summary>
    [Fact]
    public async Task AnEmptyProject_ReadsZeroThroughout_AndHasNoPrecisionYet()
    {
        var project = await AddProjectAsync("injection");

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.TotalEntries.ShouldBe(0);
        aside.TotalSessions.ShouldBe(0);
        aside.Useful.ShouldBe(0);
        aside.Marked.ShouldBe(0);
        aside.Precision.ShouldBeNull();
        aside.Lanes.Select(l => l.Entries).ShouldAllBe(e => e == 0);
    }

    [Fact]
    public async Task Promoting_FillsTheCaseFromTheEntry_ExpectingItsTopRankedWisdom()
    {
        var project = await AddProjectAsync("injection");
        var top = await AddWisdomAsync(project.Id, "the top match");
        var runnerUp = await AddWisdomAsync(project.Id, "the runner-up");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "how do I deploy?", Now,
            items: [(top.Id, 0.03), (runnerUp.Id, 0.02)]);

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        var goldenCase = await FromDb(db => db.GoldenCases.SingleAsync(Token));
        goldenCase.Id.ShouldBe(caseId.ShouldNotBeNull());
        goldenCase.QueryContext.ShouldBe("how do I deploy?");
        goldenCase.ProjectId.ShouldBe(project.Id);
        goldenCase.ExpectedWisdomId.ShouldBe(top.Id);
        goldenCase.CreatedFromInjectionId.ShouldBe(injection.Id);
        goldenCase.Note.ShouldNotBeEmpty();

        var entry = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.Single().Entries.Single();
        entry.PromotedCaseId.ShouldBe(caseId);
    }

    [Fact]
    public async Task Promoting_FallsToTheNextSurvivingItem_WhenTheTopWisdomWasDeleted()
    {
        var project = await AddProjectAsync("injection");
        var top = await AddWisdomAsync(project.Id, "the top match");
        var runnerUp = await AddWisdomAsync(project.Id, "the runner-up");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(top.Id, 0.03), (runnerUp.Id, 0.02)]);
        await Context.Wisdom.Where(w => w.Id == top.Id).ExecuteDeleteAsync(Token);

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        var goldenCase = await FromDb(db => db.GoldenCases.SingleAsync(Token));
        goldenCase.Id.ShouldBe(caseId.ShouldNotBeNull());
        goldenCase.ExpectedWisdomId.ShouldBe(runnerUp.Id);
    }

    [Fact]
    public async Task Promoting_SkipsARetiredWisdom_RecallWouldNeverSurfaceIt()
    {
        var project = await AddProjectAsync("injection");
        var top = await AddWisdomAsync(project.Id, "the top match");
        var runnerUp = await AddWisdomAsync(project.Id, "the runner-up");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(top.Id, 0.03), (runnerUp.Id, 0.02)]);
        await RetireAsync(top.Id);

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        var goldenCase = await FromDb(db => db.GoldenCases.SingleAsync(Token));
        goldenCase.Id.ShouldBe(caseId.ShouldNotBeNull());
        goldenCase.ExpectedWisdomId.ShouldBe(runnerUp.Id);
    }

    [Fact]
    public async Task AnEntryWithNoLiveWisdomLeft_CannotPromote()
    {
        var project = await AddProjectAsync("injection");
        var retired = await AddWisdomAsync(project.Id, "soon retired");
        var deleted = await AddWisdomAsync(project.Id, "soon deleted");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(retired.Id, 0.03), (deleted.Id, 0.02)]);
        await RetireAsync(retired.Id);
        await Context.Wisdom.Where(w => w.Id == deleted.Id).ExecuteDeleteAsync(Token);

        var entry = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.CanPromote.ShouldBeFalse();

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        caseId.ShouldBeNull();
        (await FromDb(db => db.GoldenCases.CountAsync(Token))).ShouldBe(0);
    }

    [Fact]
    public async Task TheListing_BoundsToTheMostRecentEntries_PrecisionCountsThemAll()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var oldest = await AddInjectionAsync(
            project.Id, "sess-old", InjectionLane.Prompt, "the cut entry", Now.AddMinutes(-1),
            items: [(wisdom.Id, 0.03)]);
        await Browser().MarkAsync(oldest.Id, InjectionVerdict.Useful, Token);
        for (var i = 0; i < InjectionBrowser.RecentEntryLimit; i++)
        {
            await AddInjectionAsync(
                project.Id, "sess-a", InjectionLane.Prompt, $"prompt {i}", Now,
                items: [(wisdom.Id, 0.03)]);
        }

        var listing = await Browser().ListAsync(new InjectionQuery(project.Id), Token);
        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.TotalEntries.ShouldBe(InjectionBrowser.RecentEntryLimit + 1);
        listing.Truncated.ShouldBeTrue();
        listing.Sessions.Sum(s => s.Entries.Count).ShouldBe(InjectionBrowser.RecentEntryLimit);
        listing.Sessions.SelectMany(s => s.Entries).ShouldAllBe(e => e.Id != oldest.Id);
        aside.Useful.ShouldBe(1);
        aside.Marked.ShouldBe(1);
        aside.Precision.ShouldNotBeNull().ShouldBe(1.0);
    }

    [Fact]
    public async Task AnEntry_CountsTheWisdomHardDeletedSinceItCarriedThem()
    {
        var project = await AddProjectAsync("injection");
        var survivor = await AddWisdomAsync(project.Id, "still here");
        var deleted = await AddWisdomAsync(project.Id, "soon deleted");
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(survivor.Id, 0.03), (deleted.Id, 0.02)]);
        await Context.Wisdom.Where(w => w.Id == deleted.Id).ExecuteDeleteAsync(Token);

        var entry = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();

        entry.Items.Count.ShouldBe(2);
        entry.WisdomSinceDeleted.ShouldBe(1);
    }

    [Fact]
    public async Task ABriefEntry_CannotPromote_ThereIsNoQueryToReplay()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now,
            items: [(wisdom.Id, 0.03)]);

        var caseId = await Browser().PromoteAsync(injection.Id, Token);

        caseId.ShouldBeNull();
        (await FromDb(db => db.GoldenCases.CountAsync(Token))).ShouldBe(0);
    }

    [Fact]
    public async Task Promoting_IsIdempotent_ARepeatClickReturnsTheExistingCase()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);

        var first = await Browser().PromoteAsync(injection.Id, Token);
        var second = await Browser().PromoteAsync(injection.Id, Token);

        second.ShouldBe(first.ShouldNotBeNull());
        (await FromDb(db => db.GoldenCases.CountAsync(Token))).ShouldBe(1);
    }

    [Fact]
    public async Task Items_CarryTheSalienceBoostTheirScoreTookFromSection7()
    {
        var project = await AddProjectAsync("injection");
        var salient = await AddWisdomAsync(project.Id, "saved on purpose");
        var plain = await AddWisdomAsync(project.Id, "distilled like the rest");
        var episode = await AddEpisodeAsync(project.Id);
        var deliberate = await AddEventAsync(episode.Id, seq: 1, salient: true);
        await AddProvenanceAsync(salient.Id, episode.Id, deliberate.Id);
        var ordinary = await AddEventAsync(episode.Id, seq: 2, salient: false);
        await AddProvenanceAsync(plain.Id, episode.Id, ordinary.Id);
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now,
            items: [(salient.Id, 0.9), (plain.Id, 0.8)]);

        var entry = (await Browser().ListAsync(new InjectionQuery(project.Id), Token))
            .Sessions.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();

        entry.Items.Single(i => i.WisdomId == salient.Id).Salient.ShouldBeTrue();
        entry.Items.Single(i => i.WisdomId == plain.Id).Salient.ShouldBeFalse();
    }

    [Fact]
    public async Task TheSearch_NarrowsOnQueryContext_AndNeverMatchesABriefWhichHasNone()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var items = new[] { (wisdom.Id, 0.03) };
        var hit = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "where do MIGRATIONS live?", Now,
            items: items);
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "how do I deploy?", Now, items: items);
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now, items: items);

        var listing = await Browser()
            .ListAsync(new InjectionQuery(project.Id, Search: "migrations"), Token);

        listing.Sessions.SelectMany(s => s.Entries).Select(e => e.Id).ShouldBe([hit.Id]);
        listing.Matching.ShouldBe(1);
        (await Browser().GetAsideAsync(project.Id, Token)).TotalEntries.ShouldBe(3);
    }

    [Fact]
    public async Task TheSearch_TakesAPercentSignLiterally_RatherThanAsAWildcard()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var items = new[] { (wisdom.Id, 0.03) };
        var literal = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "what does 100% coverage buy?", Now,
            items: items);
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "no percentage here", Now, items: items);

        var view = await Browser().ListAsync(new InjectionQuery(project.Id, Search: "%"), Token);

        view.Sessions.SelectMany(s => s.Entries).Select(e => e.Id).ShouldBe([literal.Id]);
    }

    [Fact]
    public async Task TheLaneFilter_NarrowsTheListing_WhileTheChipsCountEveryLane()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var items = new[] { (wisdom.Id, 0.03) };
        var brief = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now, items: items);
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now, items: items);
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "another prompt", Now, items: items);

        var listing = await Browser()
            .ListAsync(new InjectionQuery(project.Id, Lane: InjectionLane.Brief), Token);

        listing.Sessions.SelectMany(s => s.Entries).Select(e => e.Id).ShouldBe([brief.Id]);
        listing.Matching.ShouldBe(1);
        (await Browser().GetAsideAsync(project.Id, Token)).Lanes
            .Select(l => (l.Lane, l.Entries)).ShouldBe(
                [(InjectionLane.Brief, 1), (InjectionLane.Prompt, 2), (InjectionLane.Mcp, 0)]);
    }

    [Fact]
    public async Task AFilteredListing_IsNotTruncated_TheBoundIsMeasuredAgainstWhatMatched()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var items = new[] { (wisdom.Id, 0.03) };
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "the only match", Now, items: items);
        for (var i = 0; i < InjectionBrowser.RecentEntryLimit; i++)
        {
            await AddInjectionAsync(
                project.Id, "sess-a", InjectionLane.Prompt, $"something else {i}", Now,
                items: items);
        }

        var listing = await Browser()
            .ListAsync(new InjectionQuery(project.Id, Search: "the only match"), Token);

        listing.Listed.ShouldBe(1);
        listing.Matching.ShouldBe(1);
        listing.Truncated.ShouldBeFalse();
        (await Browser().GetAsideAsync(project.Id, Token)).TotalEntries
            .ShouldBe(InjectionBrowser.RecentEntryLimit + 1);
    }

    [Fact]
    public async Task AFilteredListingThatFillsTheBound_CountsWhatMatched_NotTheWholeProject()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var items = new[] { (wisdom.Id, 0.03) };
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Brief, null, Now, items: items);
        for (var i = 0; i <= InjectionBrowser.RecentEntryLimit; i++)
        {
            await AddInjectionAsync(
                project.Id, "sess-a", InjectionLane.Prompt, $"prompt {i}", Now, items: items);
        }

        var listing = await Browser()
            .ListAsync(new InjectionQuery(project.Id, Lane: InjectionLane.Prompt), Token);

        listing.Listed.ShouldBe(InjectionBrowser.RecentEntryLimit);
        listing.Matching.ShouldBe(InjectionBrowser.RecentEntryLimit + 1);
        listing.Truncated.ShouldBeTrue();
        (await Browser().GetAsideAsync(project.Id, Token)).TotalEntries
            .ShouldBe(InjectionBrowser.RecentEntryLimit + 2);
    }

    /// <summary>
    /// The un-narrowed listing fills the bound too, and its <c>Matching</c> is then the Project's
    /// whole count — which used to be handed over from the aside's own <c>totalEntries</c>. The
    /// split leaves the listing to count for itself; a listing that stopped counting and read the
    /// bound instead would report the screen's 100 as the match.
    /// </summary>
    [Fact]
    public async Task AnUnfilteredListingThatFillsTheBound_StillCountsTheWholeProject()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        for (var i = 0; i <= InjectionBrowser.RecentEntryLimit; i++)
        {
            await AddInjectionAsync(
                project.Id, "sess-a", InjectionLane.Prompt, $"prompt {i}", Now,
                items: [(wisdom.Id, 0.03)]);
        }

        var listing = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        listing.Listed.ShouldBe(InjectionBrowser.RecentEntryLimit);
        listing.Matching.ShouldBe(InjectionBrowser.RecentEntryLimit + 1);
        listing.Truncated.ShouldBeTrue();
    }

    /// <summary>
    /// Every figure here is one folded query's four counts, so they are asserted together: a fold
    /// that dropped a term, or crossed two of them, moves one of these without moving the rest.
    /// That the figures are the whole Project's rather than the listing's is no longer statable
    /// against — <see cref="InjectionBrowser.GetAsideAsync"/> takes a Project and nothing else, so
    /// there is no filter for a caller to pass and no reading for a test to rule out. What the
    /// filtered half of that pairing is worth pinning against is the *surface*, where the two reads
    /// meet: <c>InjectionLogTabTests.TheHeadCountsWhatMatched_AndTheAsideTheWholeProject</c>.
    /// </summary>
    [Fact]
    public async Task TheAside_CountsMarkedNoiseUnmarkedAndSessions_OverTheWholeProject()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var items = new[] { (wisdom.Id, 0.03) };
        var useful = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "p1", Now, items: items);
        var noise = await AddInjectionAsync(
            project.Id, "sess-b", InjectionLane.Prompt, "p2", Now, items: items);
        await AddInjectionAsync(project.Id, "sess-b", InjectionLane.Prompt, "p3", Now, items: items);
        await AddInjectionAsync(project.Id, "sess-c", InjectionLane.Prompt, "p4", Now, items: items);
        await Browser().MarkAsync(useful.Id, InjectionVerdict.Useful, Token);
        await Browser().MarkAsync(noise.Id, InjectionVerdict.Noise, Token);

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.TotalEntries.ShouldBe(4);
        aside.TotalSessions.ShouldBe(3);
        aside.Useful.ShouldBe(1);
        aside.Noise.ShouldBe(1);
        aside.Marked.ShouldBe(2);
        aside.Unmarked.ShouldBe(2);
    }

    /// <summary>
    /// And the four are the Project's own, not the table's: one folded query counting over an
    /// unscoped set reads right on a single-Project database and wrong on any real one.
    /// </summary>
    [Fact]
    public async Task TheAsideCounts_AreScopedToTheProject()
    {
        var (project, other) = (await AddProjectAsync("injection"), await AddProjectAsync("other"));
        await AddInjectionAsync(project.Id, "sess-a", InjectionLane.Prompt, "p1", Now);
        var elsewhere = await AddInjectionAsync(other.Id, "sess-b", InjectionLane.Prompt, "p2", Now);
        await Browser().MarkAsync(elsewhere.Id, InjectionVerdict.Useful, Token);

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.TotalEntries.ShouldBe(1);
        aside.TotalSessions.ShouldBe(1);
        aside.Useful.ShouldBe(0);
        aside.Marked.ShouldBe(0);
    }

    [Fact]
    public async Task ThePromotedCaseCount_IsThisProjectsCasesGrownFromEntries()
    {
        var (project, other) = (await AddProjectAsync("injection"), await AddProjectAsync("other"));
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        var injection = await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);
        await Browser().PromoteAsync(injection.Id, Token);
        await AddGoldenCaseAsync(project.Id, wisdom.Id);
        var elsewhere = await AddInjectionAsync(
            other.Id, "sess-b", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);
        await Browser().PromoteAsync(elsewhere.Id, Token);

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.PromotedCases.ShouldBe(1);
    }

    [Fact]
    public async Task MostRecalled_RanksThisWeeksCarriedWisdom_BoundedAndForgettingLastWeeks()
    {
        var project = await AddProjectAsync("injection");
        var ranked = new List<(Wisdom Wisdom, int Recalls)>();
        for (var recalls = MostRecalledSpread; recalls > 0; recalls--)
        {
            ranked.Add((await AddWisdomAsync(project.Id, $"recalled {recalls} times"), recalls));
        }

        var stale = await AddWisdomAsync(project.Id, "busy last week");

        for (var round = 1; round <= MostRecalledSpread; round++)
        {
            foreach (var (wisdom, recalls) in ranked.Where(r => r.Recalls >= round))
            {
                await AddInjectionAsync(
                    project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now.AddDays(-1),
                    items: [(wisdom.Id, 0.9)]);
            }
        }

        for (var i = 0; i < MostRecalledSpread + 1; i++)
        {
            await AddInjectionAsync(
                project.Id, "sess-old", InjectionLane.Brief, queryContext: null, Now.AddDays(-8),
                items: [(stale.Id, 0.9)]);
        }

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.MostRecalledThisWeek.Select(r => (r.WisdomId, r.Recalls)).ShouldBe(
            [.. ranked.OrderByDescending(r => r.Recalls)
                .Take(InjectionBrowser.MostRecalledLimit)
                .Select(r => (r.Wisdom.Id, r.Recalls))]);
        aside.MostRecalledThisWeek[0].Wisdom.ShouldNotBeNull()
            .Text.ShouldBe($"recalled {MostRecalledSpread} times");
    }

    [Fact]
    public async Task MostRecalled_KeepsAWisdomDeletedSince_ItStillDidTheWorkTheCountRecords()
    {
        var project = await AddProjectAsync("injection");
        var wisdom = await AddWisdomAsync(project.Id, "soon deleted");
        await AddInjectionAsync(
            project.Id, "sess-a", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);
        await Context.Wisdom.Where(w => w.Id == wisdom.Id).ExecuteDeleteAsync(Token);

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        var recalled = aside.MostRecalledThisWeek.ShouldHaveSingleItem();
        recalled.WisdomId.ShouldBe(wisdom.Id);
        recalled.Recalls.ShouldBe(1);
        recalled.Wisdom.ShouldBeNull();
    }

    [Fact]
    public async Task MostRecalled_IsScopedToTheProject()
    {
        var (project, other) = (await AddProjectAsync("injection"), await AddProjectAsync("other"));
        var wisdom = await AddWisdomAsync(project.Id, "a wisdom");
        await AddInjectionAsync(
            other.Id, "sess-other", InjectionLane.Prompt, "elsewhere", Now,
            items: [(wisdom.Id, 0.03)]);

        var aside = await Browser().GetAsideAsync(project.Id, Token);

        aside.MostRecalledThisWeek.ShouldBeEmpty();
    }

    private InjectionBrowser Browser() => new(Contexts, Clock);

    private async Task RetireAsync(Guid wisdomId)
        => await Context.Wisdom.Where(w => w.Id == wisdomId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.RetiredAt, (DateTimeOffset?)Now), Token);
}
