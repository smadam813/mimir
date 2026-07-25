using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §6 Distillation Queue's own surface against a real Postgres: the depth figure the tile
/// reads, the state guard on the failure parking, and the two recovery paths that now share one
/// implementation — boot's take-back of every claim versus the sweep's stale window. What the
/// claim and the <c>done</c> marker do is pinned where they are observed, in
/// <see cref="DistillationRunTests"/>.
/// </summary>
public sealed class DistillationQueueTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>
    /// Depth is what is <em>owed</em>, not what is claimable: a parked failure is owed until the
    /// sweep re-queues it, so leaving it out would report "queue empty" for the whole sweep
    /// interval after a failure. Only <c>done</c> — never re-distilled (§6) — leaves the queue,
    /// which is the same membership rule the partial index states as its filter.
    /// </summary>
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

    /// <summary>
    /// The one behavioural difference between the two callers of the shared requeue: the cutoff.
    /// The sweep may only take a claim gone quiet past <c>StaleRunningAfter</c>, because a live
    /// worker is holding the others; boot has no live worker to respect, so it takes every claim —
    /// including one stamped no earlier than boot itself, which a <em>now</em> cutoff would strand
    /// Running until the stale window caught up.
    /// </summary>
    [Fact]
    public async Task TheStaleSweep_LeavesFreshClaims_ThatBootRecoveryTakesBack()
    {
        var project = await AddProjectAsync("queue");
        var fresh = await AddEpisodeAsync(
            project.Id,
            sealedAt: Now.AddHours(-3),
            distillation: DistillationState.Running,
            distillationStartedAt: Now.AddMinutes(-10));
        // Stamped at the very instant boot reads its clock — a claim the dead process made as the
        // clock stepped back over the crash reads exactly like this, or later still.
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
        // The Episode a turn already finished with: parking it would put admitted Wisdom's
        // Episode back on the queue and re-distilling it would inflate Reinforcement (§6).
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
