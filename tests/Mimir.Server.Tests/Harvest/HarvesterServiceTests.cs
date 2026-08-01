using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using Mimir.Contracts.Health;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Health;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Harvest;

public sealed class HarvesterServiceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string Slug = "C--git-harvester";

    private readonly HealthState _health = new();
    private readonly HarvestScanTrigger _trigger = new();

    private string _root = "";
    private HarvesterService? _service;
    private ServiceProvider? _provider;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _root = Directory.CreateTempSubdirectory("mimir-harvester-").FullName;
    }

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

        Directory.Delete(_root, recursive: true);
        await base.DisposeAsync();
    }

    [Fact]
    public async Task TheBootScanReportsOnTheHarvesterTile()
    {
        WriteMemoryFile("MEMORY.md", "remembered");

        await StartServiceAsync();
        var tile = await TileAsync(t => t.State == HealthTileState.Ready);

        tile.Items.ShouldBe(1);
        tile.Changed.ShouldBe(1);
        tile.LastScanAt.ShouldBe(Now);
        tile.Summary.ShouldBe("1 item · 1 changed");
    }

    [Fact]
    public async Task ASessionEndTrigger_CausesARescanWithoutTheTimer()
    {
        WriteMemoryFile("MEMORY.md", "first");
        await StartServiceAsync();
        await TileAsync(t => t.State == HealthTileState.Ready);

        WriteMemoryFile("MEMORY.md", "second thoughts");
        Clock.Advance(TimeSpan.FromSeconds(1));
        _trigger.Request();

        var tile = await TileAsync(t => t.LastScanAt == Clock.GetUtcNow());
        tile.State.ShouldBe(HealthTileState.Ready);
        tile.Items.ShouldBe(1);
        tile.Changed.ShouldBe(1, "the edited file must have stored a new version");
    }

    [Fact]
    public async Task AFailingScan_DegradesTheTileAndKeepsTheLastGoodFigures()
    {
        WriteMemoryFile("MEMORY.md", "healthy once");
        await StartServiceAsync();
        var healthy = await TileAsync(t => t.State == HealthTileState.Ready);

        Directory.Delete(_root, recursive: true);
        _trigger.Request();

        var degraded = await TileAsync(t => t.State == HealthTileState.Degraded);
        degraded.Items.ShouldBe(healthy.Items);
        degraded.LastScanAt.ShouldBe(healthy.LastScanAt);

        Directory.CreateDirectory(_root); // so DisposeAsync still has something to delete
    }

    [Fact]
    public async Task AConversionFailure_DegradesTheTile_ButKeepsTheFreshScanFigures()
    {
        WriteMemoryFile("MEMORY.md", "scanned fine, never embedded");

        await StartServiceAsync(new ThrowingEmbeddings());

        var degraded = await TileAsync(t => t.State == HealthTileState.Degraded);
        degraded.Items.ShouldBe(1, "the scan succeeded and its figures must survive the conversion failure");
        degraded.Changed.ShouldBe(1);
        degraded.LastScanAt.ShouldBe(Now);
        degraded.Summary.ShouldBe("embedding model offline");
    }

    private sealed class ThrowingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("embedding model offline");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private async Task StartServiceAsync(IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        var services = new ServiceCollection();
        AddThrowawayStorage(services);
        services.AddScoped<ProjectResolver>();
        services.AddScoped<HarvestScanner>();
        services.AddScoped<HarvestConverter>();
        services.AddSingleton<MergeGate>();
        services.AddSingleton<IMergeArbiter>(Arbiter);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings ?? Embeddings);
        services.AddSingleton(Options.Create(new SearchOptions()));
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton(Options.Create(new HarvestOptions { Root = _root }));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new HarvesterService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _trigger,
            _health,
            Options.Create(new HarvestOptions { Root = _root }),
            Clock,
            NullLogger<HarvesterService>.Instance);
        await _service.StartAsync(Token);
    }

    private async Task<HarvesterTile> TileAsync(Func<HarvesterTile, bool> accept)
    {
        var seen = new TaskCompletionSource<HarvesterTile>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _health.Subscribe(snapshot =>
        {
            if (accept(snapshot.Harvester))
            {
                seen.TrySetResult(snapshot.Harvester);
            }
        });

        if (accept(_health.Current.Harvester))
        {
            return _health.Current.Harvester;
        }

        return await seen.Task.WaitAsync(Patience, Token);
    }

    private void WriteMemoryFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, Slug, "memory", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
