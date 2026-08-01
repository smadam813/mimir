using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

public sealed class DistillationSweepServiceTests(ThrowawayDatabaseFixture fixture)
    : PostgresTestBase(fixture)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly DistillationTrigger _trigger = new();

    private DistillationSweepService? _service;
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
    public async Task TheBootPass_RequeuesFailedEpisodes_AndPokesTheWorker()
    {
        var project = await AddProjectAsync("sweep-service");
        var failed = await AddEpisodeAsync(
            project.Id, sealedAt: Now.AddHours(-1), distillation: DistillationState.Failed);

        var services = new ServiceCollection();
        AddThrowawayStorage(services);
        services.AddScoped<DistillationSweep>();
        services.AddScoped<DistillationQueue>();
        services.AddScoped<ContestedSweep>();
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new DistillationSweepService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _trigger,
            Options.Create(new DistillationOptions()),
            Clock,
            NullLogger<DistillationSweepService>.Instance);
        await _service.StartAsync(Token);

        await _trigger.WaitAsync(Token).WaitAsync(Patience, Token);
        (await FromDb(db => db.Episodes.SingleAsync(e => e.Id == failed.Id, Token)))
            .Distillation.ShouldBe(DistillationState.Pending);
    }
}
