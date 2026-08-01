using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mimir.Contracts.Health;
using Mimir.Server.Health;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The Storage tile's owner with nothing to migrate against. No Postgres, deliberately: the
/// behaviour under test is what happens while Postgres is <em>not</em> answering.
/// </summary>
public sealed class StorageServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly HealthState _health = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _provider;
    private readonly StorageService _service;

    public StorageServiceTests()
    {
        var services = new ServiceCollection();
        // A unix-socket directory that does not exist: the connection fails immediately on
        // every platform, so a retry loop is observable without paying a TCP timeout per turn.
        void Configure(DbContextOptionsBuilder builder) => builder.UseNpgsql(
            "Host=/mimir-tests-no-such-socket-dir;Username=nobody;Password=nobody;Database=nope",
            npgsql => npgsql.UseVector());
        services.AddDbContextFactory<MimirDbContext>(Configure);
        services.AddDbContext<MimirDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
        services.AddScoped<IStorageProbe, PostgresStorageProbe>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new StorageService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _health,
            _clock,
            NullLogger<StorageService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task WhilePostgresIsStillBooting_TheTileSaysSo_AndStartUpIsNotHeldUp()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);

        _service.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse(
            "the migration runs in the background so the health strip stays visible meanwhile");

        var tile = await _health.TileAsync(
            s => s.Storage,
            t => t.State == HealthTileState.Degraded,
            Patience,
            TestContext.Current.CancellationToken);
        tile.Summary.ShouldStartWith("Postgres unavailable");
    }
}
