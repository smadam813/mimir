using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// What the sweep's loop does with a pass that throws. Deliberately outside
/// <see cref="PostgresTestBase"/>: the storage here is storage nothing can reach, so a skip-gated
/// context would hide this guard on the machines most likely to hit it.
/// </summary>
public sealed class DistillationSweepServiceFailureTests : IAsyncDisposable
{
    private readonly DistillationTrigger _trigger = new();
    private readonly ServiceProvider _provider;
    private readonly DistillationSweepService _service;

    public DistillationSweepServiceFailureTests()
    {
        var services = new ServiceCollection();
        void Configure(DbContextOptionsBuilder builder) => builder.UseNpgsql(
            "Host=/mimir-tests-no-such-socket-dir;Username=nobody;Password=nobody;Database=nope",
            npgsql => npgsql.UseVector());
        services.AddDbContextFactory<MimirDbContext>(Configure);
        services.AddDbContext<MimirDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
        services.AddScoped<DistillationSweep>();
        services.AddScoped<DistillationQueue>();
        services.AddScoped<ContestedSweep>();
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton<TimeProvider>(
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new DistillationSweepService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _trigger,
            Options.Create(new DistillationOptions()),
            _provider.GetRequiredService<TimeProvider>(),
            NullLogger<DistillationSweepService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task AFailedPass_LeavesTheLoopWaitingForTheNextTick()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);

        // The connection fails immediately, so the pass has failed well inside this window; the
        // loop surviving it is the whole claim, and letting the failure escape makes ExecuteTask
        // fault here instead of running out the clock.
        await Should.ThrowAsync<TimeoutException>(
            _service.ExecuteTask!.WaitAsync(
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }
}
