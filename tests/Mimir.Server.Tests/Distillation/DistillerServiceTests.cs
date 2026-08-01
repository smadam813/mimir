using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Health;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

public sealed class DistillerServiceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly HealthState _health = new();
    private readonly DistillationTrigger _trigger = new();

    private DistillerService? _service;
    private ServiceProvider? _provider;

    public override async ValueTask DisposeAsync()
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

        await base.DisposeAsync();
    }

    [Fact]
    public async Task ASealedEpisode_DistillsOnBoot_AndTheTileDrains()
    {
        var episode = await AddSealedEpisodeAsync();
        const string text = "Boot distillation works";
        Chat.Reply($$"""
            {"candidates":[{"kind":"lesson","scope":"project","text":"{{text}}","events":[1]}]}
            """);

        await StartServiceAsync();
        var tile = await TileAsync(t => t.State == HealthTileState.Ready && t.LastRunAt is not null);

        tile.QueueDepth.ShouldBe(0);
        tile.Summary.ShouldBe("queue empty");
        tile.LastRunAt.ShouldBe(Now);

        (await EpisodeAsync(episode.Id)).Distillation.ShouldBe(DistillationState.Done);
        (await FromDb(db => db.Wisdom.SingleAsync(Token))).Text.ShouldBe(text);
    }

    [Fact]
    public async Task ASealTrigger_WakesTheWorkerWithoutTheTimer()
    {
        await StartServiceAsync();
        await TileAsync(t => t.State == HealthTileState.Ready);

        var episode = await AddSealedEpisodeAsync();
        Chat.Reply("""{"candidates":[]}""");
        _trigger.Request();

        await TileAsync(t => t.LastRunAt is not null);
        (await EpisodeAsync(episode.Id)).Distillation.ShouldBe(DistillationState.Done);
    }

    [Fact]
    public async Task AFailingEpisode_IsParkedFailed_AndDegradesTheTile()
    {
        var episode = await AddSealedEpisodeAsync();
        Chat.Reply("no json at all");

        await StartServiceAsync();
        var tile = await TileAsync(t => t.State == HealthTileState.Degraded);

        tile.Summary.ShouldContain("not JSON");
        tile.QueueDepth.ShouldBe(
            1, "the parked Episode is still owed, and the tile is the operator's signal that it is");
        (await EpisodeAsync(episode.Id)).Distillation.ShouldBe(
            DistillationState.Failed, "a failed Episode waits for the sweep, never a hot retry");
    }

    [Fact]
    public async Task ARunningClaimFromADeadProcess_IsRequeuedAndWorkedOnBoot()
    {
        var abandoned = await AddSealedEpisodeAsync(DistillationState.Running);
        Chat.Reply("""{"candidates":[]}""");

        await StartServiceAsync();
        await TileAsync(t => t.LastRunAt is not null);

        (await EpisodeAsync(abandoned.Id)).Distillation.ShouldBe(DistillationState.Done);
    }

    [Fact]
    public async Task AfterASuccess_TheWorkerLooksAgainImmediately_DrainingTheQueue()
    {
        var project = await AddProjectAsync("drain");
        for (var seal = 3; seal >= 1; seal--)
        {
            var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddMinutes(-seal));
            await AddEventAsync(episode.Id, seq: 1, at: episode.StartedAt.AddMinutes(1));
        }

        for (var reply = 0; reply < 3; reply++)
        {
            Chat.Reply("""{"candidates":[]}""");
        }

        // The fake clock never ticks and nothing pokes the trigger, so the only way past the
        // first Episode is the loop declining to wait after something distilled.
        await StartServiceAsync();
        var tile = await TileAsync(t => t.Summary == "queue empty" && t.LastRunAt is not null);

        tile.QueueDepth.ShouldBe(0);
        (await FromDb(db => db.Episodes.CountAsync(e => e.Distillation == DistillationState.Done, Token)))
            .ShouldBe(3);
    }

    private async Task<Episode> AddSealedEpisodeAsync(DistillationState state = DistillationState.Pending)
    {
        var project = await AddProjectAsync("distiller-service");
        var episode = await AddEpisodeAsync(
            project.Id,
            sealedAt: Now.AddMinutes(-1),
            distillation: state,
            distillationStartedAt: state == DistillationState.Running ? Now.AddMinutes(-30) : null);
        await AddEventAsync(episode.Id, seq: 1, at: episode.StartedAt.AddMinutes(1),
            payload: """{"prompt":"do the thing"}""");
        return episode;
    }

    private async Task StartServiceAsync()
    {
        var services = new ServiceCollection();
        AddThrowawayStorage(services);
        services.AddScoped<DistillationQueue>();
        services.AddScoped<DistillationRun>();
        services.AddScoped<IEpisodeDistiller, EpisodeDistiller>();
        services.AddSingleton<MergeGate>();
        services.AddSingleton<IMergeArbiter>(Arbiter);
        services.AddSingleton<IChatClient>(Chat);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);
        services.AddSingleton(Options.Create(new SearchOptions()));
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new DistillerService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _trigger,
            _health,
            Clock,
            NullLogger<DistillerService>.Instance);
        await _service.StartAsync(Token);
    }

    private Task<DistillationTile> TileAsync(Func<DistillationTile, bool> accept)
        => _health.TileAsync(s => s.Distillation, accept, Patience, Token);
}
