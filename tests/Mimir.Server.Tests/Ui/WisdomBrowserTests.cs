using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

public sealed class WisdomBrowserTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SelectingAProject_ListsItsOwnWisdom_AndNoOtherProjects()
    {
        var mine = await AddProjectAsync("mine");
        var other = await AddProjectAsync("other");
        var kept = await AddWisdomAsync(mine.Id, "mine");
        await AddWisdomAsync(other.Id, "theirs");

        var listing = await Browser().ListAsync(new WisdomQuery(mine.Id), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([kept.Id]);
        listing.Entries[0].ScopeName.ShouldBe(mine.DisplayName);
    }

    [Fact]
    public async Task SelectingAProject_AlsoListsGlobal_TheSetASessionThereRecalls()
    {
        var mine = await AddProjectAsync("mine");
        var global = await AddWisdomAsync(Project.GlobalId, "everyone's");

        var listing = await Browser().ListAsync(new WisdomQuery(mine.Id), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([global.Id]);
        listing.Entries[0].ScopeName.ShouldBe("Global");
    }

    [Fact]
    public async Task SelectingGlobal_ListsGlobalAlone()
    {
        var project = await AddProjectAsync("scoped");
        var global = await AddWisdomAsync(Project.GlobalId, "a global fact");
        await AddWisdomAsync(project.Id, "a scoped fact");

        var listing = await Browser().ListAsync(new WisdomQuery(Project.GlobalId), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([global.Id]);
    }

    [Fact]
    public async Task TheHeaderCounts_PartitionTheListIntoProjectOwnedAndGlobal()
    {
        var mine = await AddProjectAsync("mine");
        await AddWisdomAsync(mine.Id, "mine, one");
        await AddWisdomAsync(mine.Id, "mine, two");
        await AddWisdomAsync(Project.GlobalId, "everyone's");

        var listing = await Browser().ListAsync(new WisdomQuery(mine.Id), Token);

        listing.ProjectOwned.ShouldBe(2);
        listing.Global.ShouldBe(1);
        (listing.ProjectOwned + listing.Global).ShouldBe(listing.Entries.Count);
    }

    [Fact]
    public async Task TheDefaultListing_ShowsActiveWisdomNewestFirst_AndExcludesRetired()
    {
        var old = await AddWisdomAsync(Project.GlobalId, "old fact", lastConfirmedAt: Now.AddDays(-2));
        var fresh = await AddWisdomAsync(Project.GlobalId, "fresh fact");
        await AddWisdomAsync(Project.GlobalId, "retired fact", retiredAt: Now);

        var listing = await Browser().ListAsync(new WisdomQuery(Project.GlobalId), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([fresh.Id, old.Id]);
    }

    [Fact]
    public async Task TheKindFilter_NarrowsTheList_ButLeavesEveryChipCounting()
    {
        var lesson = await AddWisdomAsync(Project.GlobalId, "a lesson", kind: WisdomKind.Lesson);
        await AddWisdomAsync(Project.GlobalId, "a fact");

        var listing = await Browser().ListAsync(
            new WisdomQuery(Project.GlobalId, Kind: WisdomKind.Lesson), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([lesson.Id]);
        listing.Entries[0].Kind.ShouldBe(WisdomKind.Lesson);
        listing.Kinds.Single(k => k.Kind == WisdomKind.Lesson).Count.ShouldBe(1);
        listing.Kinds.Single(k => k.Kind == WisdomKind.Fact).Count.ShouldBe(
            1, "the chips count what a click would narrow, not what the current click left");
    }

    [Fact]
    public async Task TheKindChips_CountTheWholeUniverse_EveryKindInEnumOrder()
    {
        var mine = await AddProjectAsync("chips");
        await AddWisdomAsync(mine.Id, "a scoped lesson", kind: WisdomKind.Lesson);
        await AddWisdomAsync(Project.GlobalId, "a global lesson", kind: WisdomKind.Lesson);
        await AddWisdomAsync(Project.GlobalId, "a global fact");
        await AddWisdomAsync(Project.GlobalId, "a retired lesson", kind: WisdomKind.Lesson, retiredAt: Now);

        var listing = await Browser().ListAsync(new WisdomQuery(mine.Id), Token);

        listing.Kinds.Select(k => k.Kind).ShouldBe(Enum.GetValues<WisdomKind>());
        listing.Kinds.Select(k => (k.Kind, k.Count)).ShouldBe(
            [
                (WisdomKind.Fact, 1),
                (WisdomKind.Preference, 0),
                (WisdomKind.Lesson, 2),
                (WisdomKind.Procedure, 0),
            ]);
    }

    [Fact]
    public async Task TheContestedLens_SurfacesAdjudicationSurvivors_ButNeverRetiredOnes()
    {
        var contested = await AddWisdomAsync(
            Project.GlobalId, "a disputed fact", contestedAt: Now.AddDays(-1));
        await AddWisdomAsync(Project.GlobalId, "a settled fact");
        await AddWisdomAsync(
            Project.GlobalId, "a disputed, retired fact", contestedAt: Now.AddDays(-1), retiredAt: Now);

        var listing = await Browser().ListAsync(
            new WisdomQuery(Project.GlobalId, Lens: WisdomLens.Contested), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([contested.Id]);
        listing.Entries[0].ContestedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task TheOrphanedLens_SurfacesWisdomWithNoProvenanceLeft_ButNeverRetiredOnes()
    {
        var project = await AddProjectAsync("orphan lens");
        var episode = await AddEpisodeAsync(project.Id);
        var orphan = await AddWisdomAsync(project.Id, "nothing points here");
        var sourced = await AddWisdomAsync(project.Id, "still sourced");
        await AddProvenanceAsync(sourced.Id, episode.Id);
        await AddWisdomAsync(project.Id, "orphaned and retired", retiredAt: Now);

        var listing = await Browser().ListAsync(
            new WisdomQuery(project.Id, Lens: WisdomLens.Orphaned), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([orphan.Id]);
    }

    /// <summary>
    /// "Orphaned" is one rule written twice — <c>AmbientUniverse.For</c>'s lens predicate and the
    /// <c>OrphanedProvenance</c> flag <c>WisdomBrowser.ToEntries</c> puts on every row — because
    /// neither expression can be invoked from inside the other's EF projection. The sidebar's
    /// "Orphaned N" counts the first and the row badges render the second, so a definition that
    /// drifts leaves the count disagreeing with the list it opens (#91's divergence). Seeded on
    /// the shape that separates them: harvest-only Provenance is Provenance, so narrowing either
    /// expression to Episode-borne rows flips that side alone.
    /// </summary>
    [Fact]
    public async Task TheOrphanedLens_AndTheRowsOwnFlag_AnswerTheSameQuestion()
    {
        var project = await AddProjectAsync("one rule twice");
        var orphan = await AddWisdomAsync(project.Id, "nothing points here");
        var harvested = await AddWisdomAsync(project.Id, "sourced by a harvest alone");
        await AddHarvestProvenanceAsync(harvested.Id, project.Id);

        var lens = await Browser().ListAsync(
            new WisdomQuery(project.Id, Lens: WisdomLens.Orphaned), Token);
        var listed = await Browser().ListAsync(new WisdomQuery(project.Id), Token);

        lens.Entries.Select(w => w.Id).ShouldBe([orphan.Id]);
        listed.Entries.Single(w => w.Id == orphan.Id).OrphanedProvenance.ShouldBeTrue();
        listed.Entries.Single(w => w.Id == harvested.Id).OrphanedProvenance.ShouldBeFalse();
    }

    [Fact]
    public async Task TheRetiredLens_ShowsRetiredAlone()
    {
        await AddWisdomAsync(Project.GlobalId, "an active fact");
        var retired = await AddWisdomAsync(
            Project.GlobalId, "a retired fact", retiredAt: Now, lastConfirmedAt: Now.AddDays(-1));

        var listing = await Browser().ListAsync(
            new WisdomQuery(Project.GlobalId, Lens: WisdomLens.Retired), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([retired.Id]);
        listing.Entries[0].RetiredAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Search_MatchesWordsAndSubstrings_ButNeverRetiredByDefault()
    {
        var worded = await AddWisdomAsync(Project.GlobalId, "zebras graze at dawn");
        var substring = await AddWisdomAsync(Project.GlobalId, "the quagga-zebrafish overlap");
        await AddWisdomAsync(Project.GlobalId, "unrelated filler");
        await AddWisdomAsync(Project.GlobalId, "retired zebra lore", retiredAt: Now);

        var listing = await Browser().ListAsync(
            new WisdomQuery(Project.GlobalId, Search: "zebra"), Token);

        listing.Entries.Select(w => w.Id).ShouldBe([worded.Id, substring.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task TheListing_FlagsWisdomWhoseProvenanceEmptiedThroughHardDeletes()
    {
        var project = await AddProjectAsync("orphans");
        var episode = await AddEpisodeAsync(project.Id);
        var orphan = await AddWisdomAsync(Project.GlobalId, "orphaned soon");
        await AddProvenanceAsync(orphan.Id, episode.Id);
        var sourced = await AddWisdomAsync(
            Project.GlobalId, "still sourced", lastConfirmedAt: Now.AddHours(-1));
        await AddProvenanceAsync(sourced.Id, episode.Id);
        await AddProvenanceAsync(
            sourced.Id, harvestedItemId: (await AddHarvestedItemAsync(project.Id)).Id);

        await Context.Episodes.Where(e => e.Id == episode.Id).ExecuteDeleteAsync(Token);
        var listing = await Browser().ListAsync(new WisdomQuery(Project.GlobalId), Token);

        listing.Entries.Select(w => (w.Id, w.OrphanedProvenance))
            .ShouldBe([(orphan.Id, true), (sourced.Id, false)]);
    }

    [Fact]
    public async Task TheDetail_CarriesTheChainNewestFirst_AndTheProvenanceDrillDown()
    {
        var project = await AddProjectAsync("detail");
        var episode = await AddEpisodeAsync(project.Id);
        var evt = await AddEventAsync(episode.Id, seq: 3, EventType.PostToolUse);
        var item = await AddHarvestedItemAsync(project.Id);
        var wisdom = await AddWisdomAsync(project.Id, "current text");
        Context.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = wisdom.Id,
            Version = 2,
            Text = "current text",
            CreatedAt = Now,
            Cause = WisdomVersionCause.Merged,
        });
        await Context.SaveChangesAsync(Token);
        await AddProvenanceAsync(wisdom.Id, episode.Id, evt.Id);
        await AddProvenanceAsync(wisdom.Id, harvestedItemId: item.Id);

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        detail.ShouldNotBeNull();
        detail.Entry.Id.ShouldBe(wisdom.Id);
        detail.Entry.ScopeName.ShouldBe(project.DisplayName);
        detail.Entry.OrphanedProvenance.ShouldBeFalse();
        detail.Versions.Select(v => v.Version).ShouldBe([2, 1]);
        detail.Provenance.Count.ShouldBe(2);
        var fromEvent = detail.Provenance.Single(s => s.EventId == evt.Id);
        fromEvent.EpisodeId.ShouldBe(episode.Id);
        fromEvent.EpisodeProjectId.ShouldBe(project.Id);
        fromEvent.EventSeq.ShouldBe(3);
        fromEvent.EventType.ShouldBe(EventType.PostToolUse);
        var fromHarvest = detail.Provenance.Single(s => s.HarvestedItemId == item.Id);
        fromHarvest.HarvestedPath.ShouldBe(item.Path);
    }

    [Fact]
    public async Task TheProvenanceDrillDown_CarriesTheMomentAndTheWorkingDirectory()
    {
        var project = await AddProjectAsync("recognisable");
        var episode = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-3));
        var evt = await AddEventAsync(episode.Id, seq: 2, EventType.Remember, at: Now.AddHours(-2));
        var wisdom = await AddWisdomAsync(project.Id, "a remembered fact");
        await AddProvenanceAsync(wisdom.Id, episode.Id, evt.Id);
        await AddProvenanceAsync(wisdom.Id, episode.Id);

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        detail.ShouldNotBeNull();
        var fromEvent = detail.Provenance.Single(s => s.EventId == evt.Id);
        fromEvent.EventAt.ShouldBe(Now.AddHours(-2));
        fromEvent.EpisodeCwd.ShouldBe(episode.Cwd);
        fromEvent.EpisodeStartedAt.ShouldBe(Now.AddHours(-3));
        var fromEpisode = detail.Provenance.Single(s => s.EventId is null);
        fromEpisode.EventAt.ShouldBeNull();
        fromEpisode.EpisodeCwd.ShouldBe(episode.Cwd);
        fromEpisode.EpisodeStartedAt.ShouldBe(Now.AddHours(-3));
    }

    /// <summary>
    /// A Provenance row naming an Event and no Episode still opens an Episode: the side is
    /// backfilled from the Event's own. Seeded with <c>episode_id</c> null, which is the shape the
    /// gate writes when a candidate names Events alone.
    /// </summary>
    [Fact]
    public async Task AnEventOnlyProvenance_BackfillsItsEpisode_FromTheEventItself()
    {
        var project = await AddProjectAsync("backfilled");
        var episode = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-3));
        var evt = await AddEventAsync(episode.Id, seq: 1, at: Now.AddHours(-2));
        var wisdom = await AddWisdomAsync(project.Id, "drawn from one Event");
        await AddProvenanceAsync(wisdom.Id, episodeId: null, eventId: evt.Id);

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        var link = detail.ShouldNotBeNull().Provenance.ShouldHaveSingleItem();
        link.EpisodeId.ShouldBe(episode.Id);
        link.EpisodeCwd.ShouldBe(episode.Cwd);
        link.EpisodeStartedAt.ShouldBe(Now.AddHours(-3));
    }

    /// <summary>
    /// The §8 universe is deliberately not the recall lanes' one. The lanes drop Retired rows and
    /// apply §7's native-content exclusion; a curation surface has to show both, since Wisdom a
    /// curator cannot see is Wisdom they cannot retire. Asserted against
    /// <see cref="WisdomSearch.ListAmbientAsync"/> itself rather than against a restatement of what
    /// it excludes, so the two cannot drift into agreement. The Retired half of "show both" is
    /// <see cref="TheRetiredLens_ShowsRetiredAlone"/>'s; the retired row is seeded here only as
    /// something for the lanes to exclude.
    /// </summary>
    [Fact]
    public async Task TheCurationUniverse_ShowsWhatTheRecallLanesExclude_HarvestOnlyWisdomIncluded()
    {
        var project = await AddProjectAsync("curated");
        var ordinary = await AddWisdomAsync(project.Id, "both universes hold this one");
        var episode = await AddEpisodeAsync(project.Id);
        await AddProvenanceAsync(ordinary.Id, episode.Id);
        var harvested = await AddWisdomAsync(project.Id, "harvested out of auto-memory");
        await AddHarvestProvenanceAsync(harvested.Id, project.Id);
        await AddWisdomAsync(project.Id, "retired since", retiredAt: Now);

        var lanes = await CreateWisdomSearch().ListAmbientAsync(project.Id, Token);
        var listed = await Browser().ListAsync(new WisdomQuery(project.Id), Token);

        // The ordinary row is in both, so "the lanes exclude the other two" is the only thing these
        // assertions can be reading — not a universe that came back empty for its own reasons.
        lanes.ShouldBe([ordinary.Id]);
        listed.Entries.Select(e => e.Id).ShouldBe([ordinary.Id, harvested.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task TheDetail_CountsEveryLaneThatRecalledIt_AcrossEveryProject()
    {
        var here = await AddProjectAsync("recalled here");
        var elsewhere = await AddProjectAsync("recalled elsewhere");
        var wisdom = await AddWisdomAsync(Project.GlobalId, "a much-recalled fact");
        await AddInjectionAsync(
            here.Id, lane: InjectionLane.Brief, queryContext: null, items: [(wisdom.Id, 4.0)]);
        for (var i = 0; i < 2; i++)
        {
            await AddInjectionAsync(here.Id, lane: InjectionLane.Prompt, items: [(wisdom.Id, 0.04)]);
        }

        for (var i = 0; i < 3; i++)
        {
            await AddInjectionAsync(elsewhere.Id, lane: InjectionLane.Mcp, items: [(wisdom.Id, 0.02)]);
        }

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        detail.ShouldNotBeNull();
        detail.Recall.Lanes.Select(l => (l.Lane, l.Entries)).ShouldBe(
            [(InjectionLane.Brief, 1), (InjectionLane.Prompt, 2), (InjectionLane.Mcp, 3)]);
        detail.Recall.Injections.ShouldBe(6);
    }

    [Fact]
    public async Task TheDetail_CountsTheMarksLeftOnTheEntriesThatCarriedIt()
    {
        var project = await AddProjectAsync("judged");
        var wisdom = await AddWisdomAsync(project.Id, "a judged fact");
        await AddInjectionAsync(
            project.Id, items: [(wisdom.Id, 0.04)], verdict: InjectionVerdict.Useful);
        await AddInjectionAsync(
            project.Id, items: [(wisdom.Id, 0.04)], verdict: InjectionVerdict.Useful);
        await AddInjectionAsync(
            project.Id, items: [(wisdom.Id, 0.04)], verdict: InjectionVerdict.Noise);
        await AddInjectionAsync(project.Id, items: [(wisdom.Id, 0.04)]);

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        detail.ShouldNotBeNull();
        detail.Recall.MarkedUseful.ShouldBe(2);
        detail.Recall.MarkedNoise.ShouldBe(1);
        detail.Recall.Injections.ShouldBe(
            4, "an entry nobody has judged still recalled this Wisdom");
        detail.Recall.Unmarked.ShouldBe(1, "which is what the aside says is left to judge");
    }

    [Fact]
    public async Task TheDetail_CountsNoEntryThatCarriedAnotherWisdomAlone()
    {
        var project = await AddProjectAsync("carriers");
        var subject = await AddWisdomAsync(project.Id, "the line under judgement");
        var other = await AddWisdomAsync(project.Id, "a line injected beside it");
        await AddInjectionAsync(
            project.Id, items: [(other.Id, 0.04)], verdict: InjectionVerdict.Useful);
        await AddInjectionAsync(project.Id, items: [(other.Id, 0.04), (subject.Id, 0.02)]);

        var detail = await Browser().GetAsync(subject.Id, Token);

        detail.ShouldNotBeNull();
        detail.Recall.Injections.ShouldBe(1);
        detail.Recall.MarkedUseful.ShouldBe(0);
    }

    [Fact]
    public async Task TheDetail_OfNeverRecalledWisdom_StillNamesEveryLane()
    {
        var wisdom = await AddWisdomAsync(Project.GlobalId, "nothing has recalled this");

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        detail.ShouldNotBeNull();
        detail.Recall.Lanes.Select(l => (l.Lane, l.Entries)).ShouldBe(
            [(InjectionLane.Brief, 0), (InjectionLane.Prompt, 0), (InjectionLane.Mcp, 0)]);
        detail.Recall.Injections.ShouldBe(0);
        detail.Recall.MarkedUseful.ShouldBe(0);
        detail.Recall.MarkedNoise.ShouldBe(0);
    }

    [Fact]
    public async Task TheDetail_ReadsBothEndsOfTheChain_OffItsRowsRatherThanItsLength()
    {
        var wisdom = await AddWisdomAsync(
            Project.GlobalId, "reworded since", lastConfirmedAt: Now.AddDays(-5));
        Context.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = wisdom.Id,
            Version = 3,
            Text = "reworded since",
            CreatedAt = Now,
            Cause = WisdomVersionCause.Edited,
        });
        await Context.SaveChangesAsync(Token);

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        detail.ShouldNotBeNull();
        detail.Versions.Count.ShouldBe(2);
        detail.FirstVersionAt.ShouldBe(Now.AddDays(-5));
        detail.CurrentVersion.ShouldBe(3);
    }

    [Fact]
    public async Task TheDetail_OfDeletedWisdom_AnswersNothing()
    {
        (await Browser().GetAsync(Guid.NewGuid(), Token)).ShouldBeNull();
    }

    [Fact]
    public async Task Editing_AppendsAnEditedVersion_AndReEmbedsTheNewText()
    {
        var wisdom = await AddWisdomAsync(Project.GlobalId, "the old wording");
        Embeddings.Map("the new wording", TestVectors.WithCosine(0.42));

        await Browser().EditAsync(wisdom.Id, "  the new wording  ", Token);

        var stored = await FromDb(db => db.Wisdom.SingleAsync(Token));
        stored.Text.ShouldBe("the new wording");
        stored.Embedding.ToArray()[0].ShouldBe(0.42f, 0.0001f, "the edit re-embeds (§8.1)");
        stored.Reinforcement.ShouldBe(wisdom.Reinforcement, "an edit is not a confirmation");
        stored.LastConfirmedAt.ShouldBe(wisdom.LastConfirmedAt);
        var versions = await FromDb(db => db.WisdomVersions.OrderBy(v => v.Version).ToListAsync(Token));
        versions.Select(v => (v.Version, v.Cause)).ShouldBe(
            [(1, WisdomVersionCause.Distilled), (2, WisdomVersionCause.Edited)]);
        versions[1].Text.ShouldBe("the new wording");
        versions[1].CreatedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Editing_WithoutChangingTheText_AddsNoVersion()
    {
        var wisdom = await AddWisdomAsync(Project.GlobalId, "already right");

        await Browser().EditAsync(wisdom.Id, "already right ", Token);

        (await FromDb(db => db.WisdomVersions.CountAsync(Token))).ShouldBe(1);
    }

    [Fact]
    public async Task EditingDeletedWisdom_StaysQuiet()
    {
        await Browser().EditAsync(Guid.NewGuid(), "into the void", Token);
    }

    [Fact]
    public async Task Retiring_IsReversible_AndTimestamped()
    {
        var wisdom = await AddWisdomAsync(Project.GlobalId, "a retirable fact");
        var browser = Browser();

        await browser.RetireAsync(wisdom.Id, Token);
        (await FromDb(db => db.Wisdom.SingleAsync(Token))).RetiredAt.ShouldBe(Now);

        await browser.UnretireAsync(wisdom.Id, Token);
        (await FromDb(db => db.Wisdom.SingleAsync(Token))).RetiredAt.ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_RemovesTheWisdomWithItsChain_ReferencedRecordsSurvive()
    {
        var project = await AddProjectAsync("deletes");
        var episode = await AddEpisodeAsync(project.Id);
        var wisdom = await AddWisdomAsync(Project.GlobalId, "a doomed fact");
        await AddProvenanceAsync(wisdom.Id, episode.Id);

        await Browser().DeleteAsync(wisdom.Id, Token);

        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.WisdomVersions.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.Provenance.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.Episodes.CountAsync(e => e.Id == episode.Id, Token))).ShouldBe(1);
    }

    private WisdomBrowser Browser() => new(Contexts, CreateMergeGate(), Clock);
}
