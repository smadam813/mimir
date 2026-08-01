using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

public sealed class DistillationQueueTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task QueueDepth_CountsEverySealedEpisodeNotYetDone()
    {
        var project = await AddProjectAsync("queue");
        await AddEpisodeAsync(project.Id, sealedAt: Now);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Running);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Failed);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Done);
        await AddEpisodeAsync(project.Id);

        (await NewQueue().QueueDepthAsync(Token)).ShouldBe(
            3, "pending, running and failed are all still owed; done and unsealed are not");
    }

    [Fact]
    public async Task BootRecovery_RequeuesAnAbandonedRunningClaim()
    {
        var project = await AddProjectAsync("queue");
        var abandoned = await AddEpisodeAsync(
            project.Id,
            sealedAt: Now.AddHours(-3),
            distillation: DistillationState.Running,
            distillationStartedAt: Now.AddHours(-2));

        (await NewQueue().RequeueAbandonedAsync(Token)).ShouldBe(1);

        var requeued = await EpisodeAsync(abandoned.Id);
        requeued.Distillation.ShouldBe(DistillationState.Pending);
        requeued.DistillationStartedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TheStaleSweep_LeavesFreshClaims_ThatBootRecoveryTakesBack()
    {
        var project = await AddProjectAsync("queue");
        var fresh = await AddEpisodeAsync(
            project.Id,
            sealedAt: Now.AddHours(-3),
            distillation: DistillationState.Running,
            distillationStartedAt: Now.AddMinutes(-10));
        var sameInstant = await AddEpisodeAsync(
            project.Id,
            sealedAt: Now.AddHours(-3),
            distillation: DistillationState.Running,
            distillationStartedAt: Now);
        var unstamped = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddHours(-3), distillation: DistillationState.Running);

        var queue = NewQueue();
        (await queue.RequeueStaleAsync(Token)).ShouldBe(1, "only the unstamped claim is stale");

        (await EpisodeAsync(fresh.Id)).Distillation.ShouldBe(
            DistillationState.Running, "a live worker's recent claim must not be stolen");
        (await EpisodeAsync(sameInstant.Id)).Distillation.ShouldBe(
            DistillationState.Running, "nor may the sweep steal a claim stamped this instant");
        (await EpisodeAsync(unstamped.Id)).Distillation.ShouldBe(
            DistillationState.Pending, "an unstamped claim cannot prove it is fresh");

        (await queue.RequeueAbandonedAsync(Token)).ShouldBe(2, "boot takes back every claim the sweep left");

        foreach (var recovered in new[] { fresh.Id, sameInstant.Id })
        {
            var requeued = await EpisodeAsync(recovered);
            requeued.Distillation.ShouldBe(DistillationState.Pending);
            requeued.DistillationStartedAt.ShouldBeNull();
        }
    }

    [Fact]
    public async Task TheFailureParking_OnlyTouchesAClaimStillRunning()
    {
        var project = await AddProjectAsync("queue");
        var running = await AddEpisodeAsync(
            project.Id,
            sealedAt: Now.AddHours(-1),
            distillation: DistillationState.Running,
            distillationStartedAt: Now.AddMinutes(-5));
        var done = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddHours(-2), distillation: DistillationState.Done);

        var queue = NewQueue();
        await queue.FailAsync(running.Id, Token);
        await queue.FailAsync(done.Id, Token);

        (await EpisodeAsync(running.Id)).Distillation.ShouldBe(DistillationState.Failed);
        (await EpisodeAsync(done.Id)).Distillation.ShouldBe(DistillationState.Done);
    }

    private DistillationQueue NewQueue()
        => new(Context, Clock, Options.Create(new DistillationOptions()));
}
