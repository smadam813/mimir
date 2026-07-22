using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Pgvector;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §6 sweep against a real Postgres: failed re-queues, stale running resets, idle unsealed
/// Episodes crash-Seal, done is never touched — and the folded §6.4 Contested clear rides along.
/// </summary>
public sealed class DistillationSweepTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

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
    public async Task FailedEpisodes_AreRequeued()
    {
        var failed = await AddEpisodeAsync(sealedAt: Now.AddHours(-2), state: DistillationState.Failed);

        var result = await SweepAsync();

        result.Requeued.ShouldBeGreaterThanOrEqualTo(1);
        result.QueueGrew.ShouldBeTrue();
        (await EpisodeAsync(failed.Id)).Distillation.ShouldBe(DistillationState.Pending);
    }

    [Fact]
    public async Task OnlyRunningClaims_StalePastAnHour_AreReset()
    {
        var stale = await AddEpisodeAsync(
            sealedAt: Now.AddHours(-3), state: DistillationState.Running, startedRunningAt: Now.AddHours(-2));
        var unstamped = await AddEpisodeAsync(
            sealedAt: Now.AddHours(-3), state: DistillationState.Running, startedRunningAt: null);
        var fresh = await AddEpisodeAsync(
            sealedAt: Now.AddHours(-3), state: DistillationState.Running, startedRunningAt: Now.AddMinutes(-10));

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
        var idle = await AddEpisodeAsync(sealedAt: null, startedAt: Now.AddHours(-25));
        var idleByLastEvent = await AddEpisodeAsync(sealedAt: null, startedAt: Now.AddDays(-3));
        await AddEventAsync(idleByLastEvent, at: Now.AddHours(-26));
        var aliveByLastEvent = await AddEpisodeAsync(sealedAt: null, startedAt: Now.AddDays(-3));
        await AddEventAsync(aliveByLastEvent, at: Now.AddHours(-1));
        var young = await AddEpisodeAsync(sealedAt: null, startedAt: Now.AddHours(-2));

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
        var done = await AddEpisodeAsync(sealedAt: Now.AddDays(-30), state: DistillationState.Done);

        await SweepAsync();

        (await EpisodeAsync(done.Id)).Distillation.ShouldBe(
            DistillationState.Done, "re-distilling would inflate Reinforcement (§6)");
    }

    [Fact]
    public async Task TheFoldedContestedClear_RidesAlong()
    {
        var project = TestData.NewProject("sweep-contested");
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Lesson,
            ScopeProjectId = project.Id,
            Text = $"Contested long enough {Guid.NewGuid():N}",
            Embedding = new Vector(TestVectors.WithCosine(0.5)),
            Reinforcement = 1,
            LastConfirmedAt = Now.AddDays(-20),
            ContestedAt = Now.AddDays(-15),
        };
        Context.AddRange(project, wisdom);
        await Context.SaveChangesAsync(Token);

        var result = await SweepAsync();

        result.ContestedCleared.ShouldBeGreaterThanOrEqualTo(1);
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == wisdom.Id, Token)))
            .ContestedAt.ShouldBeNull();
    }

    private async Task<SweepResult> SweepAsync()
    {
        var options = Options.Create(new DistillationOptions());
        var clock = new FakeTimeProvider(Now);
        var sweep = new DistillationSweep(Context, new ContestedSweep(Context, options, clock), options, clock);
        return await sweep.SweepAsync(Token);
    }

    private async Task<Episode> AddEpisodeAsync(
        DateTimeOffset? sealedAt,
        DistillationState state = DistillationState.Pending,
        DateTimeOffset? startedRunningAt = null,
        DateTimeOffset? startedAt = null)
    {
        var project = TestData.NewProject("sweep");
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"session-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            StartedAt = startedAt ?? (sealedAt ?? Now).AddHours(-1),
            SealedAt = sealedAt,
            SealReason = sealedAt is null ? null : "clear",
            Cwd = @"C:\git\sweep",
            Distillation = state,
            DistillationStartedAt = startedRunningAt,
        };
        Context.AddRange(project, episode);
        await Context.SaveChangesAsync(Token);
        return episode;
    }

    private async Task AddEventAsync(Episode episode, DateTimeOffset at)
    {
        Context.Add(new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = await Context.Events.CountAsync(e => e.EpisodeId == episode.Id, Token) + 1,
            Type = EventType.PostToolUse,
            At = at,
            Payload = """{"note":"activity"}""",
        });
        await Context.SaveChangesAsync(Token);
    }

    private async Task<Episode> EpisodeAsync(Guid id)
        => await FromDb(db => db.Episodes.SingleAsync(e => e.Id == id, Token));

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
