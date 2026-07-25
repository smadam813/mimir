using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §6 sweep against a real Postgres: failed re-queues, stale running resets, idle unsealed
/// Episodes crash-Seal, done is never touched — and the folded §6.4 Contested clear rides along.
/// </summary>
public sealed class DistillationSweepTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task FailedEpisodes_AreRequeued()
    {
        var project = await AddProjectAsync("sweep");
        var failed = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddHours(-2), distillation: DistillationState.Failed);

        var result = await SweepAsync();

        result.Requeued.ShouldBe(1);
        result.QueueGrew.ShouldBeTrue();
        (await EpisodeAsync(failed.Id)).Distillation.ShouldBe(DistillationState.Pending);
    }

    [Fact]
    public async Task OnlyRunningClaims_StalePastAnHour_AreReset()
    {
        var project = await AddProjectAsync("sweep");
        var stale = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddHours(-3), distillation: DistillationState.Running,
            distillationStartedAt: Now.AddHours(-2));
        var unstamped = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddHours(-3), distillation: DistillationState.Running);
        var fresh = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddHours(-3), distillation: DistillationState.Running,
            distillationStartedAt: Now.AddMinutes(-10));

        await SweepAsync();

        var reclaimed = await EpisodeAsync(stale.Id);
        reclaimed.Distillation.ShouldBe(DistillationState.Pending);
        reclaimed.DistillationStartedAt.ShouldBeNull();
        (await EpisodeAsync(unstamped.Id)).Distillation.ShouldBe(
            DistillationState.Pending, "an unstamped claim cannot prove it is fresh");
        (await EpisodeAsync(fresh.Id)).Distillation.ShouldBe(
            DistillationState.Running, "a live worker's recent claim must not be stolen");
    }

    [Fact]
    public async Task UnsealedEpisodes_IdlePastADay_AreCrashSealed()
    {
        var project = await AddProjectAsync("sweep");
        var idle = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-25));
        var idleByLastEvent = await AddEpisodeAsync(project.Id, startedAt: Now.AddDays(-3));
        await AddEventAsync(idleByLastEvent.Id, seq: 1, EventType.PostToolUse, at: Now.AddHours(-26));
        var aliveByLastEvent = await AddEpisodeAsync(project.Id, startedAt: Now.AddDays(-3));
        await AddEventAsync(aliveByLastEvent.Id, seq: 1, EventType.PostToolUse, at: Now.AddHours(-1));
        var young = await AddEpisodeAsync(project.Id, startedAt: Now.AddHours(-2));

        await SweepAsync();

        foreach (var crashed in new[] { idle.Id, idleByLastEvent.Id })
        {
            var sealedEpisode = await EpisodeAsync(crashed);
            sealedEpisode.SealedAt.ShouldBe(Now);
            sealedEpisode.SealReason.ShouldBe("crash-swept");
            sealedEpisode.Distillation.ShouldBe(DistillationState.Pending, "a crash-Sealed Episode queues normally");
        }

        (await EpisodeAsync(aliveByLastEvent.Id)).SealedAt.ShouldBeNull(
            "a recent Event proves the session alive no matter how old the Episode");
        (await EpisodeAsync(young.Id)).SealedAt.ShouldBeNull();
    }

    [Fact]
    public async Task DoneEpisodes_AreNeverTouched()
    {
        var project = await AddProjectAsync("sweep");
        var done = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddDays(-30), distillation: DistillationState.Done);

        await SweepAsync();

        (await EpisodeAsync(done.Id)).Distillation.ShouldBe(
            DistillationState.Done, "re-distilling would inflate Reinforcement (§6)");
    }

    [Fact]
    public async Task TheFoldedContestedClear_RidesAlong()
    {
        var project = await AddProjectAsync("sweep-contested");
        var wisdom = await AddWisdomAsync(
            project.Id,
            "Contested long enough",
            kind: WisdomKind.Lesson,
            lastConfirmedAt: Now.AddDays(-20),
            contestedAt: Now.AddDays(-15));

        var result = await SweepAsync();

        result.ContestedCleared.ShouldBe(1);
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == wisdom.Id, Token)))
            .ContestedAt.ShouldBeNull();
    }

    private async Task<SweepResult> SweepAsync()
    {
        var options = Options.Create(new DistillationOptions());
        var sweep = new DistillationSweep(
            Context, new ContestedSweep(Context, options, Clock), options, Clock);
        return await sweep.SweepAsync(Token);
    }

    private async Task<Episode> EpisodeAsync(Guid id)
        => await FromDb(db => db.Episodes.SingleAsync(e => e.Id == id, Token));
}
