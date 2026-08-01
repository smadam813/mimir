using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

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

    [Fact]
    public async Task AClaimedEpisode_ComesBackTrackedOnTheCallersOwnContext()
    {
        var project = await AddProjectAsync("claim-tracking");
        var seeded = await AddEpisodeAsync(project.Id, sealedAt: Now);

        var claimed = await NewQueue().ClaimNextAsync(Token);

        claimed.ShouldNotBeNull();
        Context.Entry(claimed).State.ShouldBe(
            EntityState.Unchanged,
            "the queue shares the caller's scoped context, so the claim comes back attached to it "
            + "— which is what lets the run reload the row after the gate's batch has moved it");
        Context.Episodes.Local.ShouldContain(e => e.Id == seeded.Id);
    }

    /// <summary>
    /// The queue's membership rule and the partial index's filter are one rule stated twice, in
    /// two languages. Rather than restate it a third time in prose at either site, this feeds both
    /// the same population — every (Sealed × state) combination — and reads the index's own filter
    /// back out of the catalog to run it. Changing either side alone goes red.
    /// </summary>
    [Fact]
    public async Task TheQueuesMembershipRule_IsThePartialIndexsFilter()
    {
        await AddEveryQueueStateAsync("index-agreement");

        // Named rather than "the partial index on episodes": a second one is a realistic migration
        // (the schema already carries partial indexes on two other tables), and this test would
        // then bind to whichever the catalog handed back first.
        var filter = await FromDb(db => db.Database
            .SqlQueryRaw<string>("""
                SELECT pg_get_expr(i.indpred, i.indrelid) AS "Value"
                FROM pg_index i
                JOIN pg_class ix ON ix.oid = i.indexrelid
                WHERE ix.relname = 'IX_episodes_distillation'
                """)
            .SingleAsync(Token));
        // Concatenated rather than interpolated: the filter is the catalog's own expression text,
        // and EF1002 fires on the interpolated overload regardless of where the text came from.
        var countByTheIndexsFilter = """SELECT count(*)::int AS "Value" FROM episodes WHERE """ + filter;
        var byTheIndex = await FromDb(db => db.Database
            .SqlQueryRaw<int>(countByTheIndexsFilter)
            .SingleAsync(Token));

        (await NewQueue().QueueDepthAsync(Token)).ShouldBe(
            byTheIndex, "the queue counts exactly the rows its partial index admits");
    }

    /// <summary>
    /// The header's <c>Queued</c> readout restates the same predicate a third time, because the
    /// browsers open their own contexts and cannot call the scoped queue. Same shape of agreement
    /// test, so the restatement cannot drift silently.
    /// </summary>
    [Fact]
    public async Task TheHeadersQueuedReadout_AgreesWithTheQueuesDepth()
    {
        await AddEveryQueueStateAsync("header-agreement");

        var pipeline = await new ChassisBrowser(Contexts, Clock).GetHeaderPipelineAsync(Token);

        pipeline.Queued.ShouldBe(await NewQueue().QueueDepthAsync(Token));
    }

    /// <summary>
    /// Every (Sealed × state) combination, so an agreement test feeds both sides of a restated
    /// predicate the same population rather than a convenient corner of it — and a distinct number
    /// of rows per state, so the two sides can disagree by *which* state they exclude and not only
    /// by how many. One row each would make every "exclude exactly one state" predicate count the
    /// same, leaving `!= Done` → `!= Failed` green on both sides at once.
    /// </summary>
    private async Task AddEveryQueueStateAsync(string name)
    {
        var project = await AddProjectAsync(name);
        var rows = 1;
        foreach (var state in Enum.GetValues<DistillationState>())
        {
            for (var i = 0; i < rows; i++)
            {
                await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: state);
                await AddEpisodeAsync(project.Id, distillation: state);
            }

            rows *= 2;
        }
    }

    private DistillationQueue NewQueue()
        => new(Context, Clock, Options.Create(new DistillationOptions()));
}
