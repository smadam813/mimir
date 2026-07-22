using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.AI;
using Mimir.Contracts.Health;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Health;
using Mimir.Server.Storage;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Harvest;

/// <summary>
/// The §5 service loop end to end: the boot scan reports on the Harvester tile, and a SessionEnd
/// trigger causes a rescan with no timer involved — the fake clock never ticks, so any second
/// scan can only have come from the trigger.
/// </summary>
public sealed class HarvesterServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly string _slug = $"C--git-harvester-{Guid.NewGuid():N}";
    private readonly HealthState _health = new();
    private readonly HarvestScanTrigger _trigger = new();

    private string _root = "";
    private HarvesterService? _service;
    private ServiceProvider? _provider;

    public ValueTask InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("mimir-harvester-").FullName;
        return ValueTask.CompletedTask;
    }

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

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TheBootScanReportsOnTheHarvesterTile()
    {
        WriteMemoryFile("MEMORY.md", "remembered");

        await StartServiceAsync();
        var tile = await TileAsync(t => t.State == HealthTileState.Ready);

        tile.Items.ShouldBe(1);
        tile.Changed.ShouldBe(1);
        tile.LastScanAt.ShouldBe(_clock.GetUtcNow());
        tile.Summary.ShouldBe("1 item · 1 changed");
    }

    [Fact]
    public async Task ASessionEndTrigger_CausesARescanWithoutTheTimer()
    {
        WriteMemoryFile("MEMORY.md", "first");
        await StartServiceAsync();
        await TileAsync(t => t.State == HealthTileState.Ready);

        WriteMemoryFile("MEMORY.md", "second thoughts");
        // One second is far short of the 5-minute interval, so the timer cannot be what rescans;
        // it only re-stamps the clock so the second scan is distinguishable from the first.
        _clock.Advance(TimeSpan.FromSeconds(1));
        _trigger.Request();

        var tile = await TileAsync(t => t.LastScanAt == _clock.GetUtcNow());
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

        // The scan itself succeeds; only the §5 handoff to the Merge Gate fails. The tile must
        // say so without discarding what the scan just found.
        await StartServiceAsync(new ThrowingEmbeddings());

        var degraded = await TileAsync(t => t.State == HealthTileState.Degraded);
        degraded.Items.ShouldBe(1, "the scan succeeded and its figures must survive the conversion failure");
        degraded.Changed.ShouldBe(1);
        degraded.LastScanAt.ShouldBe(_clock.GetUtcNow());
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
        if (fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(TestPostgres.SkipMessage(reason));
        }

        var services = new ServiceCollection();
        services.AddDbContext<MimirDbContext>(options =>
            options.UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<ProjectResolver>();
        services.AddScoped<HarvestScanner>();
        // The scan loop hands changed items straight to the Merge Gate (§5), so the converter's
        // whole graph rides along — with deterministic fake embeddings in place of Ollama.
        services.AddScoped<HarvestConverter>();
        services.AddScoped<MergeGate>();
        services.AddScoped<IMergeArbiter>(_ => new FakeArbiter());
        services.AddScoped<WisdomSearch>();
        services.AddSingleton(embeddings ?? new FakeEmbeddings());
        services.AddSingleton(Options.Create(new SearchOptions()));
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton(Options.Create(new HarvestOptions { Root = _root }));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new HarvesterService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _trigger,
            _health,
            Options.Create(new HarvestOptions { Root = _root }),
            _clock,
            NullLogger<HarvesterService>.Instance);
        await _service.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits (in real time — the service loop runs on real threads) for a tile state.</summary>
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

        return await seen.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    private void WriteMemoryFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, _slug, "memory", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
