using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.3 against a real Postgres: the injection log's per-session listing with sizes and
/// hydrated items, the one-click §9 marks with <c>verdict_at</c>, the injection-precision
/// inputs, and promote-to-golden — filled from the entry's <c>query_context</c> and
/// <c>project_id</c>, refused for Brief entries, idempotent on repeat clicks.
/// </summary>
public sealed class InjectionBrowserTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>
    /// The top recall count the "most recalled" fixture seeds — one past
    /// <see cref="InjectionBrowser.MostRecalledLimit"/>, so the bound has to drop exactly one and
    /// the ranking decides which.
    /// </summary>
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

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.Useful.ShouldBe(2);
        view.Marked.ShouldBe(3);
        view.Precision.ShouldNotBeNull().ShouldBe(2.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public async Task PrecisionIsNull_UntilAnythingIsMarked()
    {
        var project = await AddProjectAsync("injection");

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.Marked.ShouldBe(0);
        view.Precision.ShouldBeNull();
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

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.TotalEntries.ShouldBe(InjectionBrowser.RecentEntryLimit + 1);
        view.Truncated.ShouldBeTrue();
        view.Sessions.Sum(s => s.Entries.Count).ShouldBe(InjectionBrowser.RecentEntryLimit);
        view.Sessions.SelectMany(s => s.Entries).ShouldAllBe(e => e.Id != oldest.Id);
        // The cut entry's mark still feeds the §9 precision inputs — and the figure itself, not
        // only the two counts it divides, is what the bound must not be able to move.
        view.Useful.ShouldBe(1);
        view.Marked.ShouldBe(1);
        view.Precision.ShouldNotBeNull().ShouldBe(1.0);
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
        // The other one has Provenance too — so what marks it apart is the Event's salience, not
        // the mere existence of a link back.
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

        var view = await Browser()
            .ListAsync(new InjectionQuery(project.Id, Search: "migrations"), Token);

        view.Sessions.SelectMany(s => s.Entries).Select(e => e.Id).ShouldBe([hit.Id]);
        view.Matching.ShouldBe(1);
        // The aside is the whole Project's, whatever the box says — §9's stat is whole-history.
        view.TotalEntries.ShouldBe(3);
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

        var view = await Browser()
            .ListAsync(new InjectionQuery(project.Id, Lane: InjectionLane.Brief), Token);

        view.Sessions.SelectMany(s => s.Entries).Select(e => e.Id).ShouldBe([brief.Id]);
        view.Matching.ShouldBe(1);
        // Every lane keeps a chip, including the one this Project has never used: a chip that
        // vanished at zero would read as "no such lane".
        view.Lanes.Select(l => (l.Lane, l.Entries)).ShouldBe(
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

        var view = await Browser()
            .ListAsync(new InjectionQuery(project.Id, Search: "the only match"), Token);

        view.Listed.ShouldBe(1);
        view.Matching.ShouldBe(1);
        view.Truncated.ShouldBeFalse();
        view.TotalEntries.ShouldBe(InjectionBrowser.RecentEntryLimit + 1);
    }

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

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.TotalEntries.ShouldBe(4);
        view.TotalSessions.ShouldBe(3);
        view.Useful.ShouldBe(1);
        view.Noise.ShouldBe(1);
        view.Marked.ShouldBe(2);
        view.Unmarked.ShouldBe(2);
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
        // A hand-written case, and another Project's promotion: neither grew from this log.
        await AddGoldenCaseAsync(project.Id, wisdom.Id);
        var elsewhere = await AddInjectionAsync(
            other.Id, "sess-b", InjectionLane.Prompt, "a prompt", Now,
            items: [(wisdom.Id, 0.03)]);
        await Browser().PromoteAsync(elsewhere.Id, Token);

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.PromotedCases.ShouldBe(1);
    }

    [Fact]
    public async Task MostRecalled_RanksThisWeeksCarriedWisdom_BoundedAndForgettingLastWeeks()
    {
        var project = await AddProjectAsync("injection");
        // One more Wisdom than the bound admits, each on a distinct count, so the ranking has
        // something to get wrong: a listing without its ORDER BY has to pick five of six out of
        // Postgres's own grouping order, and the sixth is not the one that should have been cut.
        var ranked = new List<(Wisdom Wisdom, int Recalls)>();
        for (var recalls = MostRecalledSpread; recalls > 0; recalls--)
        {
            ranked.Add((await AddWisdomAsync(project.Id, $"recalled {recalls} times"), recalls));
        }

        var stale = await AddWisdomAsync(project.Id, "busy last week");

        // Seeded weakest-first, and each Wisdom's injections interleaved with the others', so
        // neither insertion order nor a per-Wisdom run can hand back a descending list by accident.
        for (var round = 1; round <= MostRecalledSpread; round++)
        {
            foreach (var (wisdom, recalls) in ranked.Where(r => r.Recalls >= round))
            {
                await AddInjectionAsync(
                    project.Id, "sess-a", InjectionLane.Brief, queryContext: null, Now.AddDays(-1),
                    items: [(wisdom.Id, 0.9)]);
            }
        }

        // Recalled more than any of them, one day outside the window.
        for (var i = 0; i < MostRecalledSpread + 1; i++)
        {
            await AddInjectionAsync(
                project.Id, "sess-old", InjectionLane.Brief, queryContext: null, Now.AddDays(-8),
                items: [(stale.Id, 0.9)]);
        }

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.MostRecalledThisWeek.Select(r => (r.WisdomId, r.Recalls)).ShouldBe(
            [.. ranked.OrderByDescending(r => r.Recalls)
                .Take(InjectionBrowser.MostRecalledLimit)
                .Select(r => (r.Wisdom.Id, r.Recalls))]);
        view.MostRecalledThisWeek[0].Wisdom.ShouldNotBeNull()
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

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        var recalled = view.MostRecalledThisWeek.ShouldHaveSingleItem();
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

        var view = await Browser().ListAsync(new InjectionQuery(project.Id), Token);

        view.MostRecalledThisWeek.ShouldBeEmpty();
    }

    private InjectionBrowser Browser() => new(Contexts, Clock);

    private async Task RetireAsync(Guid wisdomId)
        => await Context.Wisdom.Where(w => w.Id == wisdomId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.RetiredAt, (DateTimeOffset?)Now), Token);
}
