using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §6 queue turn against a real Postgres: Seal → pending → done with candidates reaching the
/// gate carrying Event Provenance; failure → failed with nothing admitted; later chunks' candidates
/// merging with the Wisdom earlier chunks just created (the Merge Gate as the reduce).
/// </summary>
public sealed class DistillationRunTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ASealedPendingEpisode_DistillsToDone_WithEventProvenance()
    {
        var project = await AddProjectAsync("distiller");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        var evt = await AddEventAsync(episode.Id, seq: 1);
        const string text = "Always pin the SDK feature band";
        Chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{text}}","events":[1]}]}
            """);

        var attempt = (await NewRun().RunNextAsync(Token)).ShouldNotBeNull();

        attempt.Succeeded.ShouldBeTrue();
        attempt.EpisodeId.ShouldBe(episode.Id);
        attempt.Candidates.ShouldBe(1);

        var done = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == episode.Id, Token));
        done.Distillation.ShouldBe(DistillationState.Done);
        done.DistilledAt.ShouldBe(Now);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(text);
        wisdom.Kind.ShouldBe(WisdomKind.Lesson);
        wisdom.ScopeProjectId.ShouldBe(project.Id);
        var provenance = await FromDb(db => db.Provenance.SingleAsync(Token));
        provenance.WisdomId.ShouldBe(wisdom.Id);
        provenance.EpisodeId.ShouldBe(episode.Id);
        provenance.EventId.ShouldBe(evt.Id);
        provenance.HarvestedItemId.ShouldBeNull();
    }

    [Fact]
    public async Task TheQueue_TakesTheOldestSeal_AndIgnoresUnsealedAndDone()
    {
        var project = await AddProjectAsync("distiller");
        var newer = await AddEpisodeAsync(project.Id, sealedAt: Now.AddMinutes(-5));
        var older = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-2));
        await AddEpisodeAsync(project.Id);
        await AddEpisodeAsync(project.Id, sealedAt: Now.AddDays(-1), distillation: DistillationState.Done);
        await AddEventAsync(newer.Id, seq: 1, EventType.Stop);
        await AddEventAsync(older.Id, seq: 1, EventType.Stop);
        Chat.Reply("""{"candidates":[]}""");
        Chat.Reply("""{"candidates":[]}""");

        var run = NewRun();
        (await run.RunNextAsync(Token)).ShouldNotBeNull().EpisodeId.ShouldBe(older.Id);
        (await run.RunNextAsync(Token)).ShouldNotBeNull().EpisodeId.ShouldBe(newer.Id);
        (await run.RunNextAsync(Token)).ShouldBeNull("unsealed and done Episodes are not work");
    }

    [Fact]
    public async Task AFailure_MarksFailed_AndAdmitsNothing_EvenFromTheChunksThatParsed()
    {
        var project = await AddProjectAsync("distiller");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        // Two chunks at this budget: the first answers cleanly, the second is garbage — the
        // Episode must fail whole, with the first chunk's candidate never admitted, so the
        // sweep's re-queue redoes it without inflating Reinforcement.
        await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse, payload: PayloadOfChars(4000));
        await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse, payload: PayloadOfChars(4000));
        Chat.Reply("""{"candidates":[{"kind":"fact","scope":"project","text":"From the good chunk.","events":[1]}]}""");
        Chat.Reply("no json at all");

        var attempt = (await NewRun(new DistillationOptions { ChunkTokens = 1024 }).RunNextAsync(Token))
            .ShouldNotBeNull();

        attempt.Succeeded.ShouldBeFalse();
        attempt.Error.ShouldNotBeNull();
        var failed = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == episode.Id, Token));
        failed.Distillation.ShouldBe(DistillationState.Failed);
        failed.DistilledAt.ShouldBeNull();
        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(0);
    }

    [Fact]
    public async Task AFailureInsideTheBatch_LeavesTheEpisodeStillOwedDistillation()
    {
        var project = await AddProjectAsync("distiller");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse, payload: PayloadOfChars(4000));
        await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse, payload: PayloadOfChars(4000));
        // Both chunks parse and the whole Episode reaches the gate as one batch, where the
        // second candidate matches the first and the arbiter throws. The existing failure test
        // fails at the distiller, before the gate; this one fails inside the batch, which is the
        // path that used to run in the Run's own transaction and now runs in the gate's — the
        // Episode must come back Failed and owed, never Done, with the first candidate's
        // already-saved admission taken back with the failing one.
        const string born = "The lock serializes the gate";
        const string confirming = "Gate admissions are serialized";
        Embeddings.Map(born, TestVectors.Basis);
        Embeddings.Map(confirming, TestVectors.WithCosine(0.9));
        Chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{born}}","events":[1]}]}
            """);
        Chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{confirming}}","events":[2]}]}
            """);
        Arbiter.Failure = new MergeArbiterException("the model returned nothing usable");

        var attempt = (await NewRun(new DistillationOptions { ChunkTokens = 1024 }).RunNextAsync(Token))
            .ShouldNotBeNull();

        attempt.Succeeded.ShouldBeFalse();
        var failed = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == episode.Id, Token));
        failed.Distillation.ShouldBe(
            DistillationState.Failed, "the Episode is still owed distillation, so the sweep re-queues it");
        failed.DistilledAt.ShouldBeNull();
        (await FromDb(db => db.Wisdom.CountAsync(Token)))
            .ShouldBe(0, "the first candidate's admission rolls back with the failing one");
    }

    [Fact]
    public async Task LaterChunksCandidates_MergeWithEarlierChunksWisdom()
    {
        var project = await AddProjectAsync("distiller");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        var first = await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse, payload: PayloadOfChars(4000));
        var second = await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse, payload: PayloadOfChars(4000));
        const string born = "The build needs the full SDK version";
        const string confirming = "Full SDK version required by the build";
        Embeddings.Map(born, TestVectors.Basis);
        Embeddings.Map(confirming, TestVectors.WithCosine(0.85));
        Chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{born}}","events":[1]}]}
            """);
        Chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{confirming}}","events":[2]}]}
            """);

        var attempt = (await NewRun(new DistillationOptions { ChunkTokens = 1024 }).RunNextAsync(Token))
            .ShouldNotBeNull();

        attempt.Succeeded.ShouldBeTrue();
        attempt.Candidates.ShouldBe(2);
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(born, "the second chunk's candidate reinforced instead of duplicating");
        wisdom.Reinforcement.ShouldBe(2);
        var events = await FromDb(db => db.Provenance.Select(p => p.EventId).ToListAsync(Token));
        events.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task QueueDepth_CountsSealedPendingAndRunningOnly()
    {
        var project = await AddProjectAsync("distiller");
        await AddEpisodeAsync(project.Id, sealedAt: Now);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Running);
        await AddEpisodeAsync(project.Id, sealedAt: Now, distillation: DistillationState.Done);
        await AddEpisodeAsync(project.Id);

        (await NewRun().QueueDepthAsync(Token)).ShouldBe(2);
    }

    [Fact]
    public async Task BootRecovery_RequeuesAnAbandonedRunningClaim()
    {
        var project = await AddProjectAsync("distiller");
        var abandoned = await AddEpisodeAsync(
            project.Id,
            sealedAt: Now.AddHours(-3),
            distillation: DistillationState.Running,
            distillationStartedAt: Now.AddHours(-2));

        (await NewRun().RequeueAbandonedAsync(Token)).ShouldBe(1);

        var requeued = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == abandoned.Id, Token));
        requeued.Distillation.ShouldBe(DistillationState.Pending);
        requeued.DistillationStartedAt.ShouldBeNull();
    }

    private DistillationRun NewRun(DistillationOptions? options = null)
    {
        var settings = options ?? new DistillationOptions();
        return new DistillationRun(
            Context,
            new EpisodeDistiller(Chat, Options.Create(settings)),
            CreateMergeGate(distillation: settings),
            Clock,
            NullLogger<DistillationRun>.Instance);
    }

    private static string PayloadOfChars(int chars) => $$"""{"note":"{{new string('x', chars)}}"}""";
}
