using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Health;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The §6 worker loop end to end: a Sealed Episode distills on boot and the tile drains live; a
/// Seal's trigger wakes the worker with no timer involved (the fake clock never ticks); a failure
/// parks the Episode as failed and degrades the tile; a Running claim left by a dead process is
/// re-queued and worked on the next boot.
/// </summary>
public sealed class DistillerServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeChatClient _chat = new();
    private readonly HealthState _health = new();
    private readonly DistillationTrigger _trigger = new();

    private DistillerService? _service;
    private ServiceProvider? _provider;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASealedEpisode_DistillsOnBoot_AndTheTileDrains()
    {
        var episode = await AddSealedEpisodeAsync();
        var text = $"Boot distillation works {Guid.NewGuid():N}";
        _chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{text}}","events":[1]}]}
            """);

        await StartServiceAsync();
        var tile = await TileAsync(t => t.State == HealthTileState.Ready && t.LastRunAt is not null);

        tile.QueueDepth.ShouldBe(0);
        tile.Summary.ShouldBe("queue empty");
        tile.LastRunAt.ShouldBe(_clock.GetUtcNow());

        (await EpisodeAsync(episode.Id)).Distillation.ShouldBe(DistillationState.Done);
        (await FromDb(db => db.Wisdom.CountAsync(w => w.Text == text, Token))).ShouldBe(1);
    }

    [Fact]
    public async Task ASealTrigger_WakesTheWorkerWithoutTheTimer()
    {
        await StartServiceAsync();
        await TileAsync(t => t.State == HealthTileState.Ready);

        var episode = await AddSealedEpisodeAsync();
        _chat.Reply("""{"candidates":[]}""");
        // The fake clock never ticks, so only the trigger can be what wakes the worker.
        _trigger.Request();

        await TileAsync(t => t.LastRunAt is not null);
        (await EpisodeAsync(episode.Id)).Distillation.ShouldBe(DistillationState.Done);
    }

    [Fact]
    public async Task AFailingEpisode_IsParkedFailed_AndDegradesTheTile()
    {
        var episode = await AddSealedEpisodeAsync();
        _chat.Reply("no json at all");

        await StartServiceAsync();
        var tile = await TileAsync(t => t.State == HealthTileState.Degraded);

        tile.Summary.ShouldContain("not JSON");
        (await EpisodeAsync(episode.Id)).Distillation.ShouldBe(
            DistillationState.Failed, "a failed Episode waits for the sweep, never a hot retry");
    }

    [Fact]
    public async Task ARunningClaimFromADeadProcess_IsRequeuedAndWorkedOnBoot()
    {
        var abandoned = await AddSealedEpisodeAsync(state: DistillationState.Running);
        _chat.Reply("""{"candidates":[]}""");

        await StartServiceAsync();
        await TileAsync(t => t.LastRunAt is not null);

        (await EpisodeAsync(abandoned.Id)).Distillation.ShouldBe(DistillationState.Done);
    }

    private async Task<Episode> AddSealedEpisodeAsync(DistillationState state = DistillationState.Pending)
    {
        if (fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(TestPostgres.SkipMessage(reason));
        }

        var project = TestData.NewProject("distiller-service");
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"session-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            StartedAt = _clock.GetUtcNow().AddHours(-1),
            SealedAt = _clock.GetUtcNow().AddMinutes(-1),
            SealReason = "clear",
            Cwd = @"C:\git\distiller-service",
            Distillation = state,
            DistillationStartedAt = state == DistillationState.Running
                ? _clock.GetUtcNow().AddMinutes(-30)
                : null,
        };
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = 1,
            Type = EventType.UserPromptSubmit,
            At = episode.StartedAt.AddMinutes(1),
            Payload = """{"prompt":"do the thing"}""",
        };

        await using var context = fixture.CreateContext();
        context.AddRange(project, episode, evt);
        await context.SaveChangesAsync(Token);
        return episode;
    }

    private async Task StartServiceAsync()
    {
        if (fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(TestPostgres.SkipMessage(reason));
        }

        var services = new ServiceCollection();
        services.AddDbContext<MimirDbContext>(options =>
            options.UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        // The worker's whole scoped graph — with the scripted chat model and deterministic fake
        // embeddings in place of Ollama.
        services.AddScoped<DistillationRun>();
        services.AddScoped<EpisodeDistiller>();
        services.AddScoped<MergeGate>();
        services.AddScoped<IMergeArbiter>(_ => new FakeArbiter());
        services.AddScoped<WisdomSearch>();
        services.AddSingleton<IChatClient>(_chat);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddings());
        services.AddSingleton(Options.Create(new SearchOptions()));
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new DistillerService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _trigger,
            _health,
            _clock,
            NullLogger<DistillerService>.Instance);
        await _service.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits (in real time — the service loop runs on real threads) for a tile state.</summary>
    private async Task<DistillationTile> TileAsync(Func<DistillationTile, bool> accept)
    {
        var seen = new TaskCompletionSource<DistillationTile>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _health.Subscribe(snapshot =>
        {
            if (accept(snapshot.Distillation))
            {
                seen.TrySetResult(snapshot.Distillation);
            }
        });

        if (accept(_health.Current.Distillation))
        {
            return _health.Current.Distillation;
        }

        return await seen.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    private async Task<Episode> EpisodeAsync(Guid id)
        => await FromDb(db => db.Episodes.SingleAsync(e => e.Id == id, Token));

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = fixture.CreateContext();
        return await query(context);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
