using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §6 queue turn against a real Postgres: Seal → pending → done with candidates reaching the
/// gate carrying Event Provenance; failure → failed with nothing admitted; later chunks' candidates
/// merging with the Wisdom earlier chunks just created (the Merge Gate as the reduce).
/// </summary>
public sealed class DistillationRunTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeChatClient _chat = new();
    private readonly FakeEmbeddings _embeddings = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private MimirDbContext? _context;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASealedPendingEpisode_DistillsToDone_WithEventProvenance()
    {
        await Context.ResetWisdomAsync(Token);
        var episode = await AddEpisodeAsync(sealedAt: Now.AddHours(-1));
        var evt = await AddEventAsync(episode, 1, EventType.UserPromptSubmit);
        var text = $"Always pin the SDK feature band {Guid.NewGuid():N}";
        _chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{text}}","events":[1]}]}
            """);

        var attempt = (await NewRun().RunNextAsync(Token)).ShouldNotBeNull();

        attempt.Succeeded.ShouldBeTrue();
        attempt.EpisodeId.ShouldBe(episode.Id);
        attempt.Candidates.ShouldBe(1);

        var done = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == episode.Id, Token));
        done.Distillation.ShouldBe(DistillationState.Done);
        done.DistilledAt.ShouldBe(Now);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == text, Token));
        wisdom.Kind.ShouldBe(WisdomKind.Lesson);
        wisdom.ScopeProjectId.ShouldBe(episode.ProjectId);
        var provenance = await FromDb(db => db.Provenance.SingleAsync(p => p.WisdomId == wisdom.Id, Token));
        provenance.EpisodeId.ShouldBe(episode.Id);
        provenance.EventId.ShouldBe(evt.Id);
        provenance.HarvestedItemId.ShouldBeNull();
    }

    [Fact]
    public async Task TheQueue_TakesTheOldestSeal_AndIgnoresUnsealedAndDone()
    {
        await Context.ResetWisdomAsync(Token);
        var newer = await AddEpisodeAsync(sealedAt: Now.AddMinutes(-5));
        var older = await AddEpisodeAsync(sealedAt: Now.AddHours(-2));
        await AddEpisodeAsync(sealedAt: null);
        await AddEpisodeAsync(sealedAt: Now.AddDays(-1), state: DistillationState.Done);
        await AddEventAsync(newer, 1, EventType.Stop);
        await AddEventAsync(older, 1, EventType.Stop);
        _chat.Reply("""{"candidates":[]}""");
        _chat.Reply("""{"candidates":[]}""");

        var run = NewRun();
        (await run.RunNextAsync(Token)).ShouldNotBeNull().EpisodeId.ShouldBe(older.Id);
        (await run.RunNextAsync(Token)).ShouldNotBeNull().EpisodeId.ShouldBe(newer.Id);
        (await run.RunNextAsync(Token)).ShouldBeNull("unsealed and done Episodes are not work");
    }

    [Fact]
    public async Task AFailure_MarksFailed_AndAdmitsNothing_EvenFromTheChunksThatParsed()
    {
        await Context.ResetWisdomAsync(Token);
        var episode = await AddEpisodeAsync(sealedAt: Now.AddHours(-1));
        // Two chunks at this budget: the first answers cleanly, the second is garbage — the
        // Episode must fail whole, with the first chunk's candidate never admitted, so the
        // sweep's re-queue redoes it without inflating Reinforcement.
        await AddEventAsync(episode, 1, EventType.PostToolUse, PayloadOfChars(4000));
        await AddEventAsync(episode, 2, EventType.PostToolUse, PayloadOfChars(4000));
        _chat.Reply("""{"candidates":[{"kind":"fact","scope":"project","text":"From the good chunk.","events":[1]}]}""");
        _chat.Reply("no json at all");

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
    public async Task LaterChunksCandidates_MergeWithEarlierChunksWisdom()
    {
        await Context.ResetWisdomAsync(Token);
        var episode = await AddEpisodeAsync(sealedAt: Now.AddHours(-1));
        var first = await AddEventAsync(episode, 1, EventType.PostToolUse, PayloadOfChars(4000));
        var second = await AddEventAsync(episode, 2, EventType.PostToolUse, PayloadOfChars(4000));
        var born = $"The build needs the full SDK version {Guid.NewGuid():N}";
        var confirming = $"Full SDK version required by the build {Guid.NewGuid():N}";
        _embeddings.Map(born, TestVectors.Basis);
        _embeddings.Map(confirming, TestVectors.WithCosine(0.85));
        _chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{born}}","events":[1]}]}
            """);
        _chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{confirming}}","events":[2]}]}
            """);

        var attempt = (await NewRun(new DistillationOptions { ChunkTokens = 1024 }).RunNextAsync(Token))
            .ShouldNotBeNull();

        attempt.Succeeded.ShouldBeTrue();
        attempt.Candidates.ShouldBe(2);
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(born, "the second chunk's candidate reinforced instead of duplicating");
        wisdom.Reinforcement.ShouldBe(2);
        var events = await FromDb(db => db.Provenance
            .Where(p => p.WisdomId == wisdom.Id)
            .Select(p => p.EventId)
            .ToListAsync(Token));
        events.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task QueueDepth_CountsSealedPendingAndRunningOnly()
    {
        await AddEpisodeAsync(sealedAt: Now);
        await AddEpisodeAsync(sealedAt: Now, state: DistillationState.Running);
        await AddEpisodeAsync(sealedAt: Now, state: DistillationState.Done);
        await AddEpisodeAsync(sealedAt: null);

        // Other tests of this class seed their own Episodes into the shared class database, so
        // assert against a floor from an independent query rather than an exact figure.
        var expected = await FromDb(db => db.Episodes.CountAsync(
            e => e.SealedAt != null
                && (e.Distillation == DistillationState.Pending || e.Distillation == DistillationState.Running),
            Token));

        (await NewRun().QueueDepthAsync(Token)).ShouldBe(expected);
        expected.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task BootRecovery_RequeuesAnAbandonedRunningClaim()
    {
        var abandoned = await AddEpisodeAsync(sealedAt: Now.AddHours(-3), state: DistillationState.Running);
        await FromDb(db => db.Episodes
            .Where(e => e.Id == abandoned.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.DistillationStartedAt, Now.AddHours(-2)), Token));

        (await NewRun().RequeueAbandonedAsync(Token)).ShouldBeGreaterThanOrEqualTo(1);

        var requeued = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == abandoned.Id, Token));
        requeued.Distillation.ShouldBe(DistillationState.Pending);
        requeued.DistillationStartedAt.ShouldBeNull();
    }

    private DistillationRun NewRun(DistillationOptions? options = null)
    {
        var distillation = Options.Create(options ?? new DistillationOptions());
        var search = new WisdomSearch(Context, Options.Create(new SearchOptions()));
        var gate = new MergeGate(Context, _embeddings, search, _arbiter, distillation, _clock);
        var distiller = new EpisodeDistiller(_chat, distillation);
        return new DistillationRun(
            Context, distiller, gate, _embeddings, _clock, NullLogger<DistillationRun>.Instance);
    }

    private static string PayloadOfChars(int chars) => $$"""{"note":"{{new string('x', chars)}}"}""";

    private async Task<Episode> AddEpisodeAsync(
        DateTimeOffset? sealedAt, DistillationState state = DistillationState.Pending)
    {
        var project = TestData.NewProject("distiller");
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"session-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            StartedAt = (sealedAt ?? Now).AddHours(-1),
            SealedAt = sealedAt,
            SealReason = sealedAt is null ? null : "clear",
            Cwd = @"C:\git\distiller",
            Distillation = state,
        };
        Context.AddRange(project, episode);
        await Context.SaveChangesAsync(Token);
        return episode;
    }

    private async Task<Event> AddEventAsync(
        Episode episode, int seq, EventType type, string payload = """{"note":"payload"}""")
    {
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = seq,
            Type = type,
            At = episode.StartedAt.AddMinutes(seq),
            Payload = payload,
            Salient = type == EventType.Remember,
        };
        Context.Add(evt);
        await Context.SaveChangesAsync(Token);
        return evt;
    }

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = fixture.CreateContext();
        return await query(context);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private MimirDbContext Context
    {
        get
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip(TestPostgres.SkipMessage(reason));
            }

            return _context ??= fixture.CreateContext();
        }
    }
}
