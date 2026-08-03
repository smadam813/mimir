using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Health;

namespace Mimir.Server.Tests.Distillation;

/// <summary>The §6 worker over <see cref="UnreachableStorage"/>: what the loop does with a pass
/// that reaches no storage at all.</summary>
public sealed class DistillerServiceFailureTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);

    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EarlierRun = Now.AddMinutes(-7);

    private readonly HealthState _health = new();
    private readonly LoopClock _clock = new(Now);
    private readonly CapturedLog<DistillerService> _log = new();
    private readonly ServiceProvider _provider;
    private readonly DistillerService _service;

    public DistillerServiceFailureTests()
    {
        var services = new ServiceCollection();
        UnreachableStorage.Add(services);
        services.AddScoped<DistillationQueue>();
        services.AddScoped<DistillationRun>();
        services.AddScoped<IEpisodeDistiller>(_ => new FakeDistiller());
        services.AddSingleton<MergeGate>();
        services.AddSingleton<IMergeArbiter>(new FakeArbiter());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddings());
        services.AddSingleton(Options.Create(new SearchOptions()));
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new DistillerService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new DistillationTrigger(),
            _health,
            _clock,
            _log);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task APassThatCannotReachStorage_DegradesTheTileWithoutZeroingIt_AndTheLoopRetries()
    {
        // A tile with figures on it, so a pass that learned nothing can be seen not to blank them.
        _health.Update(snapshot => snapshot with
        {
            Distillation = new DistillationTile
            {
                State = HealthTileState.Ready,
                Summary = "queue empty",
                QueueDepth = 4,
                LastRunAt = EarlierRun,
            },
        });

        await _service.StartAsync(TestContext.Current.CancellationToken);

        var tile = await _health.TileAsync(
            s => s.Distillation,
            t => t.State == HealthTileState.Degraded,
            Patience,
            TestContext.Current.CancellationToken);
        tile.QueueDepth.ShouldBe(4, "a pass that never reached the queue learned nothing about its depth");
        tile.LastRunAt.ShouldBe(EarlierRun, "and nothing ran, so the last run is still the last run");
        _log.Warnings.ShouldHaveSingleItem().ShouldContain("Distillation pass failed");

        _service.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse(
            "the loop ends on a shutdown cancellation and on nothing else");

        await _clock.StraddleAsync(
            DistillerService.FailureRetryInterval,
            Margin,
            () => _log.Warnings.Count >= 2,
            Patience,
            TestContext.Current.CancellationToken);

        _service.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
    }
}
