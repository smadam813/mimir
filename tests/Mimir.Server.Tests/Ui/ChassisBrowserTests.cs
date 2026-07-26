using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8 against a real Postgres: every number the chassis renders — the Project sidebar (moved
/// here from <c>EpisodeBrowserTests</c> with the methods, #89), the header's whole-install
/// pipeline, the tab strip's per-Project counts, and the sidebar's three swapping second groups.
/// Each query is seeded with both a row that should count and one that should not, so dropping
/// either half of a predicate reddens a specific test rather than only ever passing.
/// </summary>
public sealed class ChassisBrowserTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task TheSidebar_ListsGlobalFirst_ThenProjectsByName()
    {
        var beta = await AddProjectAsync("beta");
        var alpha = await AddProjectAsync("alpha");

        var projects = await Browser().ListProjectsAsync(Token);

        projects[0].Id.ShouldBe(Project.GlobalId);
        projects[0].IsGlobal.ShouldBeTrue();
        var index = projects.Select(p => p.Id).ToList();
        index.IndexOf(alpha.Id).ShouldBeLessThan(index.IndexOf(beta.Id));
    }

    [Fact]
    public async Task ASingleProject_IsFetchedByItsId_OrNotAtAll()
    {
        var project = await AddProjectAsync("lookup");

        var found = await Browser().GetProjectAsync(project.Id, Token);
        var missing = await Browser().GetProjectAsync(Guid.NewGuid(), Token);

        found.ShouldNotBeNull();
        found.DisplayName.ShouldBe("lookup");
        found.IsGlobal.ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task TheSidebarsWisdomCount_IsThisProjectsActiveWisdom_NotAnotherProjectsOrRetired()
    {
        var project = await AddProjectAsync("counted");
        var other = await AddProjectAsync("uncounted");
        await AddWisdomAsync(project.Id, "active, mine");
        await AddWisdomAsync(project.Id, "retired, mine", retiredAt: Now);
        await AddWisdomAsync(other.Id, "active, someone else's");

        var projects = await Browser().ListProjectsAsync(Token);
        var single = await Browser().GetProjectAsync(project.Id, Token);

        projects.Single(p => p.Id == project.Id).WisdomCount.ShouldBe(1);
        single.ShouldNotBeNull();
        single.WisdomCount.ShouldBe(1);
    }

    [Fact]
    public async Task TheHeaderPipeline_CountsEveryEpisode_AcrossEveryProject()
    {
        var a = await AddProjectAsync("a");
        var b = await AddProjectAsync("b");
        await AddEpisodeAsync(a.Id);
        await AddEpisodeAsync(a.Id);
        await AddEpisodeAsync(b.Id);

        var pipeline = await Browser().GetHeaderPipelineAsync(Token);

        pipeline.Episodes.ShouldBe(3);
    }

    [Fact]
    public async Task TheHeaderPipeline_QueuesSealedNotDone_FailedIncluded_UnsealedAndDoneExcluded()
    {
        var project = await AddProjectAsync("queue");
        await AddEpisodeAsync(project.Id, sealedAt: null, distillation: DistillationState.Pending); // unsealed
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Pending);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Running);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Failed);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Done);

        var pipeline = await Browser().GetHeaderPipelineAsync(Token);

        pipeline.Queued.ShouldBe(3);
    }

    [Fact]
    public async Task TheHeaderPipeline_Distilling_IsTrueOnlyWhileAClaimIsHeld_NotMerelyBacklogged()
    {
        var project = await AddProjectAsync("distilling");
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Pending);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Failed);

        var backlogged = await Browser().GetHeaderPipelineAsync(Token);

        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Running);

        var claimed = await Browser().GetHeaderPipelineAsync(Token);

        backlogged.Distilling.ShouldBeFalse();
        claimed.Distilling.ShouldBeTrue();
    }

    [Fact]
    public async Task TheHeaderPipeline_CountsActiveWisdom_AcrossEveryProject_ExcludingRetired()
    {
        var a = await AddProjectAsync("a");
        var b = await AddProjectAsync("b");
        await AddWisdomAsync(a.Id, "active a");
        await AddWisdomAsync(b.Id, "active b");
        await AddWisdomAsync(b.Id, "retired b", retiredAt: Now);

        var pipeline = await Browser().GetHeaderPipelineAsync(Token);

        pipeline.Wisdom.ShouldBe(2);
    }

    [Fact]
    public async Task TheHeaderPipeline_RecalledToday_ExcludesYesterday()
    {
        var project = await AddProjectAsync("recalled");
        await AddInjectionAsync(project.Id, sessionId: "today", at: Now);
        await AddInjectionAsync(project.Id, sessionId: "yesterday", at: Now.AddDays(-1));

        var pipeline = await Browser().GetHeaderPipelineAsync(Token);

        pipeline.RecalledToday.ShouldBe(1);
    }

    [Fact]
    public async Task TheTabStripCounts_AreThisProjectsAlone()
    {
        var project = await AddProjectAsync("mine");
        var other = await AddProjectAsync("theirs");
        await AddWisdomAsync(project.Id, "active");
        await AddWisdomAsync(project.Id, "retired", retiredAt: Now);
        await AddEpisodeAsync(project.Id);
        await AddInjectionAsync(project.Id, sessionId: "s1");
        await AddInjectionAsync(project.Id, sessionId: "s2");
        await AddWisdomAsync(other.Id, "not mine");
        await AddEpisodeAsync(other.Id);
        await AddInjectionAsync(other.Id, sessionId: "s3");

        var counts = await Browser().GetSurfaceCountsAsync(project.Id, Token);

        counts.Wisdom.ShouldBe(1);
        counts.Episodes.ShouldBe(1);
        counts.Injections.ShouldBe(2);
    }

    [Fact]
    public async Task WisdomAttention_SeparatesContestedFromRetired_AcrossTheAmbientUniverse()
    {
        var project = await AddProjectAsync("attention");
        var other = await AddProjectAsync("other");
        await AddWisdomAsync(project.Id, "contested, active", contestedAt: Now);
        await AddWisdomAsync(project.Id, "contested, but retired", contestedAt: Now, retiredAt: Now);
        await AddWisdomAsync(project.Id, "plain retired", retiredAt: Now);
        await AddWisdomAsync(other.Id, "contested elsewhere", contestedAt: Now);

        var attention = await Browser().GetWisdomAttentionAsync(project.Id, Token);

        attention.Contested.ShouldBe(1);
        attention.Retired.ShouldBe(2);
    }

    /// <summary>
    /// Each of these three is the label on a link into the Wisdom surface's matching lens, so the
    /// count has to be the length of the list that link opens — Global included (#91). Only the
    /// Global arm of the shared universe keeper can redden this one: nothing of the Project's own
    /// is seeded.
    /// </summary>
    [Fact]
    public async Task WisdomAttention_CountsGlobalToo_SoEachFigureIsItsOwnLinksList()
    {
        var project = await AddProjectAsync("attention");
        await AddWisdomAsync(Project.GlobalId, "contested everywhere", contestedAt: Now);
        await AddWisdomAsync(Project.GlobalId, "retired everywhere", retiredAt: Now);

        var attention = await Browser().GetWisdomAttentionAsync(project.Id, Token);

        attention.Contested.ShouldBe(1);
        attention.Retired.ShouldBe(1);
        attention.Orphaned.ShouldBe(1, "the contested Global row has no Provenance either");
    }

    [Fact]
    public async Task WisdomAttention_OrphanedIsActiveWisdomWithNoProvenance()
    {
        var project = await AddProjectAsync("orphans");
        var sourced = await AddWisdomAsync(project.Id, "has provenance");
        await AddProvenanceAsync(sourced.Id);
        await AddWisdomAsync(project.Id, "no provenance");
        await AddWisdomAsync(project.Id, "no provenance, but retired", retiredAt: Now);

        var attention = await Browser().GetWisdomAttentionAsync(project.Id, Token);

        attention.Orphaned.ShouldBe(1);
    }

    [Fact]
    public async Task CaptureAttention_SplitsRunningFailedAndQueueDepth_ScopedToThisProject()
    {
        var project = await AddProjectAsync("capture");
        var other = await AddProjectAsync("other");
        await AddEpisodeAsync(project.Id, sealedAt: null, distillation: DistillationState.Pending); // unsealed
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Pending);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Running);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Failed);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Done);
        await AddEpisodeAsync(other.Id, sealedAt: Now, distillation: DistillationState.Running);

        var attention = await Browser().GetCaptureAttentionAsync(project.Id, Token);

        attention.Running.ShouldBe(1);
        attention.Failed.ShouldBe(1);
        attention.QueueDepth.ShouldBe(1);
    }

    [Fact]
    public async Task RecallAttention_CountsMarkedUsefulAndNoise_ScopedToThisProject()
    {
        var project = await AddProjectAsync("recall");
        var other = await AddProjectAsync("other");
        await AddInjectionAsync(project.Id, sessionId: "useful");
        await AddInjectionAsync(project.Id, sessionId: "noise");
        await AddInjectionAsync(project.Id, sessionId: "unmarked");
        await AddInjectionAsync(other.Id, sessionId: "elsewhere");
        await MarkAsync(project.Id, "useful", InjectionVerdict.Useful);
        await MarkAsync(project.Id, "noise", InjectionVerdict.Noise);
        await MarkAsync(other.Id, "elsewhere", InjectionVerdict.Useful);

        var attention = await Browser().GetRecallAttentionAsync(project.Id, Token);

        attention.MarkedUseful.ShouldBe(1);
        attention.MarkedNoise.ShouldBe(1);
    }

    [Fact]
    public async Task RecallAttention_CountsEntriesWhoseWisdomHasSinceGone_NotOnesStillLive()
    {
        var project = await AddProjectAsync("since-deleted");
        var stillLive = await AddWisdomAsync(project.Id, "still here");
        await AddInjectionAsync(
            project.Id, sessionId: "live", items: [(stillLive.Id, 0.9)]);
        await AddInjectionAsync(
            project.Id, sessionId: "gone", items: [(Guid.NewGuid(), 0.9)]);
        await AddInjectionAsync(project.Id, sessionId: "no-items");

        var attention = await Browser().GetRecallAttentionAsync(project.Id, Token);

        attention.WisdomSinceDeleted.ShouldBe(1);
    }

    [Fact]
    public async Task FirstRun_IsTrueWithOnlyGlobal_FalseOnceARealProjectExists()
    {
        (await Browser().IsFirstRunAsync(Token)).ShouldBeTrue();

        await AddProjectAsync("the first hook");

        (await Browser().IsFirstRunAsync(Token)).ShouldBeFalse();
    }

    private async Task MarkAsync(Guid projectId, string sessionId, InjectionVerdict verdict)
    {
        var injection = await Context.Injections
            .SingleAsync(i => i.ProjectId == projectId && i.SessionId == sessionId, Token);
        injection.Verdict = verdict;
        injection.VerdictAt = Now;
        await Context.SaveChangesAsync(Token);
    }

    private ChassisBrowser Browser() => new(Contexts, Clock);
}
