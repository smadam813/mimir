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

/// <summary>
/// The §5 service loop end to end: the boot scan reports on the Harvester tile, and a SessionEnd
/// trigger causes a rescan with no timer involved — the fake clock never ticks, so any second
/// scan can only have come from the trigger.
/// </summary>
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
        // Base first — see HarvestScannerTests: a temp root created before the skip outlives it.
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
        // One second is far short of the 5-minute interval, so the timer cannot be what rescans;
        // it only re-stamps the clock so the second scan is distinguishable from the first.
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

        // The scan itself succeeds; only the §5 handoff to the Merge Gate fails. The tile must
        // say so without discarding what the scan just found.
        await StartServiceAsync(new ThrowingEmbeddings());

        var degraded = await TileAsync(t => t.State == HealthTileState.Degraded);
        degraded.Items.ShouldBe(1, "the scan succeeded and its figures must survive the conversion failure");
        degraded.Changed.ShouldBe(1);
        degraded.LastScanAt.ShouldBe(Now);
        degraded.Summary.ShouldBe("embedding model offline");
    }

    [Fact]
    public async Task ACancellationThatIsNotTheShutdowns_DegradesTheTile_AndTheLoopKeepsScanning()
    {
        WriteMemoryFile("MEMORY.md", "scanned fine, cancelled mid-embed");
        var embeds = 0;
        Embeddings.OnGenerate = _ =>
        {
            if (Interlocked.Increment(ref embeds) == 1)
            {
                throw new OperationCanceledException("the query timed out");
            }
        };

        // A query timeout surfaces as an OperationCanceledException with nobody's shutdown behind
        // it. That is a failed scan to degrade and retry, not a reason to tear the host down.
        await StartServiceAsync();
        await TileAsync(t => t.State == HealthTileState.Degraded);

        Clock.Advance(TimeSpan.FromSeconds(1));
        _trigger.Request();

        var recovered = await TileAsync(t => t.State == HealthTileState.Ready);
        recovered.LastScanAt.ShouldBe(Clock.GetUtcNow(), "the loop was still alive to rescan");
    }

    [Fact]
    public async Task TheHostsOwnShutdown_EndsTheLoopWithoutDegradingTheTile()
    {
        WriteMemoryFile("MEMORY.md", "scanned, then the host went down mid-embed");
        var scanning = new TaskCompletionSource();
        var stopping = new TaskCompletionSource();
        Embeddings.OnGenerate = _ =>
        {
            scanning.TrySetResult();
            stopping.Task.Wait(Patience);
            throw new OperationCanceledException("the host is going down");
        };

        await StartServiceAsync();
        await scanning.Task.WaitAsync(Patience, Token);

        // StopAsync cancels the stopping token before it awaits, so releasing the scan here makes
        // its cancellation a genuine shutdown — the one OperationCanceledException ScanAsync's
        // filter must let past, for ExecuteAsync's filter to catch. The two are inverses, and
        // this is the half that pins the coupling: weaken either and the tile degrades.
        var stopped = _service!.StopAsync(CancellationToken.None);
        stopping.TrySetResult();
        await stopped;

        _health.Current.Harvester.State.ShouldNotBe(
            HealthTileState.Degraded, "a shutdown is not a failed scan to report and retry");
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
        // The scoped context the converter reads through, and the factory the gate opens each
        // Admission batch on.
        AddThrowawayStorage(services);
        services.AddScoped<ProjectResolver>();
        services.AddScoped<HarvestScanner>();
        // The scan loop hands changed items straight to the Merge Gate (§5), so the converter's
        // whole graph rides along — with deterministic fake embeddings in place of Ollama.
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

    /// <summary>Waits (in real time — the service loop runs on real threads) for a tile state.</summary>
    private Task<HarvesterTile> TileAsync(Func<HarvesterTile, bool> accept)
        => _health.TileAsync(s => s.Harvester, accept, Patience, Token);

    private void WriteMemoryFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, Slug, "memory", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
