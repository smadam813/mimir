using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

public sealed class DistillationRunTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private readonly FakeDistiller _distiller = new();

    [Fact]
    public async Task ASealedPendingEpisode_DistillsToDone_WithEventProvenance()
    {
        var project = await AddProjectAsync("distiller");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        await AddEventAsync(episode.Id, seq: 2);
        var evt = await AddEventAsync(episode.Id, seq: 1);
        const string text = "Always pin the SDK feature band";
        _distiller.Enqueue(new WisdomCandidate(
            WisdomKind.Lesson, project.Id, text, EpisodeId: episode.Id, EventIds: [evt.Id]));

        var attempt = (await NewRun().RunNextAsync(Token)).ShouldNotBeNull();

        attempt.Succeeded.ShouldBeTrue();
        attempt.EpisodeId.ShouldBe(episode.Id);
        attempt.Candidates.ShouldBe(1);

        var call = _distiller.Calls.ShouldHaveSingleItem();
        call.EpisodeId.ShouldBe(episode.Id);
        call.ProjectIdentity.ShouldBe(project.Identity);
        call.Events.Select(e => e.Seq).ShouldBe([1, 2]);

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
        _distiller.Enqueue();
        _distiller.Enqueue();

        var run = NewRun();
        (await run.RunNextAsync(Token)).ShouldNotBeNull().EpisodeId.ShouldBe(older.Id);
        (await run.RunNextAsync(Token)).ShouldNotBeNull().EpisodeId.ShouldBe(newer.Id);
        (await run.RunNextAsync(Token)).ShouldBeNull("unsealed and done Episodes are not work");
    }

    [Fact]
    public async Task AnUnusableAnswer_MarksFailed_AndAdmitsNothing()
    {
        var project = await AddProjectAsync("distiller");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse);
        _distiller.Failure = new DistillerException("the distiller's answer is not JSON");

        var attempt = (await NewRun().RunNextAsync(Token)).ShouldNotBeNull();

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
        var first = await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse);
        var second = await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse);
        const string born = "The lock serializes the gate";
        const string confirming = "Gate admissions are serialized";
        Embeddings.Map(born, TestVectors.Basis);
        Embeddings.Map(confirming, TestVectors.WithCosine(0.9));
        _distiller.Enqueue(
            new WisdomCandidate(WisdomKind.Lesson, project.Id, born, EpisodeId: episode.Id, EventIds: [first.Id]),
            new WisdomCandidate(
                WisdomKind.Lesson, project.Id, confirming, EpisodeId: episode.Id, EventIds: [second.Id]));
        Arbiter.Failure = new MergeArbiterException("the model returned nothing usable");

        var attempt = (await NewRun().RunNextAsync(Token)).ShouldNotBeNull();

        attempt.Succeeded.ShouldBeFalse();
        var failed = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == episode.Id, Token));
        failed.Distillation.ShouldBe(
            DistillationState.Failed, "the Episode is still owed distillation, so the sweep re-queues it");
        failed.DistilledAt.ShouldBeNull();
        (await FromDb(db => db.Wisdom.CountAsync(Token)))
            .ShouldBe(0, "the first candidate's admission rolls back with the failing one");
    }

    [Fact]
    public async Task OneEpisodesCandidates_MergeWithEachOther_InsideTheOneBatch()
    {
        var project = await AddProjectAsync("distiller");
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        var first = await AddEventAsync(episode.Id, seq: 1, EventType.PostToolUse);
        var second = await AddEventAsync(episode.Id, seq: 2, EventType.PostToolUse);
        const string born = "The build needs the full SDK version";
        const string confirming = "Full SDK version required by the build";
        Embeddings.Map(born, TestVectors.Basis);
        Embeddings.Map(confirming, TestVectors.WithCosine(0.85));
        _distiller.Enqueue(
            new WisdomCandidate(WisdomKind.Lesson, project.Id, born, EpisodeId: episode.Id, EventIds: [first.Id]),
            new WisdomCandidate(
                WisdomKind.Lesson, project.Id, confirming, EpisodeId: episode.Id, EventIds: [second.Id]));

        var attempt = (await NewRun().RunNextAsync(Token)).ShouldNotBeNull();

        attempt.Succeeded.ShouldBeTrue();
        attempt.Candidates.ShouldBe(2);
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(born, "the second candidate reinforced instead of duplicating");
        wisdom.Reinforcement.ShouldBe(2);
        var events = await FromDb(db => db.Provenance.Select(p => p.EventId).ToListAsync(Token));
        events.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    private DistillationRun NewRun() => new(
        Context,
        new DistillationQueue(Context, Clock, Options.Create(new DistillationOptions())),
        _distiller,
        CreateMergeGate(),
        NullLogger<DistillationRun>.Instance);
}
