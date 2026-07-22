using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The sweep's hosted loop: the boot pass runs the <see cref="DistillationSweep"/> and, having
/// grown the queue, pokes the worker's trigger.
/// </summary>
public sealed class DistillationSweepServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly DistillationTrigger _trigger = new();

    private DistillationSweepService? _service;
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
    public async Task TheBootPass_RequeuesFailedEpisodes_AndPokesTheWorker()
    {
        if (fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(TestPostgres.SkipMessage(reason));
        }

        var project = TestData.NewProject("sweep-service");
        var failed = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"session-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            StartedAt = _clock.GetUtcNow().AddHours(-2),
            SealedAt = _clock.GetUtcNow().AddHours(-1),
            SealReason = "clear",
            Cwd = @"C:\git\sweep-service",
            Distillation = DistillationState.Failed,
        };
        await using (var context = fixture.CreateContext())
        {
            context.AddRange(project, failed);
            await context.SaveChangesAsync(Token);
        }

        var services = new ServiceCollection();
        services.AddDbContext<MimirDbContext>(options =>
            options.UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<DistillationSweep>();
        services.AddScoped<ContestedSweep>();
        services.AddSingleton(Options.Create(new DistillationOptions()));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new DistillationSweepService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _trigger,
            Options.Create(new DistillationOptions()),
            _clock,
            NullLogger<DistillationSweepService>.Instance);
        await _service.StartAsync(Token);

        // The poke proves the pass both ran and reported queue growth; the row state proves what
        // it did. WaitAsync (not a poll) so a missing poke fails loudly at the patience limit.
        await _trigger.WaitAsync(Token).WaitAsync(Patience, Token);
        await using var fresh = fixture.CreateContext();
        (await fresh.Episodes.SingleAsync(e => e.Id == failed.Id, Token))
            .Distillation.ShouldBe(DistillationState.Pending);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
