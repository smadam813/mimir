using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.1 against a real Postgres: the queries behind the Wisdom browser — search plus the
/// five filters (kind/scope/project/contested/retired), the orphaned-provenance flag, the detail
/// with its version chain and Provenance drill-down — and the curation actions: edit (new
/// version, <c>cause=edited</c>, re-embed), Retire/unretire, and the confirmed Delete.
/// </summary>
public sealed class WisdomBrowserTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task TheDefaultListing_ShowsActiveWisdomNewestFirst_AndExcludesRetired()
    {
        var old = await AddWisdomAsync(Project.GlobalId, "old fact", lastConfirmedAt: Now.AddDays(-2));
        var fresh = await AddWisdomAsync(Project.GlobalId, "fresh fact");
        await AddWisdomAsync(Project.GlobalId, "retired fact", retiredAt: Now);

        var listing = await Browser().ListAsync(new WisdomQuery(), Token);

        listing.Select(w => w.Id).ShouldBe([fresh.Id, old.Id]);
    }

    [Fact]
    public async Task TheKindFilter_NarrowsToTheKind()
    {
        var lesson = await AddWisdomAsync(Project.GlobalId, "a lesson", kind: WisdomKind.Lesson);
        await AddWisdomAsync(Project.GlobalId, "a fact");

        var listing = await Browser().ListAsync(new WisdomQuery(Kind: WisdomKind.Lesson), Token);

        listing.Select(w => w.Id).ShouldBe([lesson.Id]);
        listing[0].Kind.ShouldBe(WisdomKind.Lesson);
    }

    [Fact]
    public async Task TheScopeFilter_SeparatesGlobalFromProjectScoped()
    {
        var project = await AddProjectAsync("scope");
        var global = await AddWisdomAsync(Project.GlobalId, "a global fact");
        var scoped = await AddWisdomAsync(project.Id, "a scoped fact");

        var globals = await Browser().ListAsync(new WisdomQuery(Scope: WisdomScopeFilter.Global), Token);
        var projectScoped = await Browser().ListAsync(
            new WisdomQuery(Scope: WisdomScopeFilter.ProjectScoped), Token);

        globals.Select(w => w.Id).ShouldBe([global.Id]);
        globals[0].ScopeName.ShouldBe("Global");
        projectScoped.Select(w => w.Id).ShouldBe([scoped.Id]);
        projectScoped[0].ScopeName.ShouldBe(project.DisplayName);
    }

    [Fact]
    public async Task TheProjectFilter_NarrowsToOneProjectsScope()
    {
        var mine = await AddProjectAsync("mine");
        var other = await AddProjectAsync("other");
        var kept = await AddWisdomAsync(mine.Id, "mine");
        await AddWisdomAsync(other.Id, "theirs");
        await AddWisdomAsync(Project.GlobalId, "everyone's");

        var listing = await Browser().ListAsync(new WisdomQuery(ProjectId: mine.Id), Token);

        listing.Select(w => w.Id).ShouldBe([kept.Id]);
    }

    [Fact]
    public async Task TheContestedFilter_SurfacesOnlyAdjudicationSurvivors()
    {
        var contested = await AddWisdomAsync(
            Project.GlobalId, "a disputed fact", contestedAt: Now.AddDays(-1));
        await AddWisdomAsync(Project.GlobalId, "a settled fact");

        var listing = await Browser().ListAsync(new WisdomQuery(ContestedOnly: true), Token);

        listing.Select(w => w.Id).ShouldBe([contested.Id]);
        listing[0].ContestedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task TheRetirementFilter_ShowsRetiredAlone_OrEverything()
    {
        var active = await AddWisdomAsync(Project.GlobalId, "an active fact");
        var retired = await AddWisdomAsync(
            Project.GlobalId, "a retired fact", retiredAt: Now, lastConfirmedAt: Now.AddDays(-1));

        var retiredOnly = await Browser().ListAsync(
            new WisdomQuery(Retirement: WisdomRetirementFilter.Retired), Token);
        var all = await Browser().ListAsync(
            new WisdomQuery(Retirement: WisdomRetirementFilter.All), Token);

        retiredOnly.Select(w => w.Id).ShouldBe([retired.Id]);
        all.Select(w => w.Id).ShouldBe([active.Id, retired.Id]);
    }

    [Fact]
    public async Task Search_MatchesWordsAndSubstrings_ButNeverRetiredByDefault()
    {
        var worded = await AddWisdomAsync(Project.GlobalId, "zebras graze at dawn");
        var substring = await AddWisdomAsync(Project.GlobalId, "the quagga-zebrafish overlap");
        await AddWisdomAsync(Project.GlobalId, "unrelated filler");
        await AddWisdomAsync(Project.GlobalId, "retired zebra lore", retiredAt: Now);

        var listing = await Browser().ListAsync(new WisdomQuery(Search: "zebra"), Token);

        listing.Select(w => w.Id).ShouldBe([worded.Id, substring.Id], ignoreOrder: true);
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
        var listing = await Browser().ListAsync(new WisdomQuery(), Token);

        listing.Select(w => (w.Id, w.OrphanedProvenance))
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
        fromEvent.SessionId.ShouldBe(episode.SessionId);
        fromEvent.EventSeq.ShouldBe(3);
        fromEvent.EventType.ShouldBe(EventType.PostToolUse);
        var fromHarvest = detail.Provenance.Single(s => s.HarvestedItemId == item.Id);
        fromHarvest.HarvestedPath.ShouldBe(item.Path);
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

    /// <summary>
    /// The browser over the fixture's database, its edit wired to a real Merge Gate — the gate is
    /// where the edit's re-embed, version append and lock live now (§6, ADR-0004).
    /// </summary>
    private WisdomBrowser Browser() => new(Contexts, CreateMergeGate(), Clock);
}
