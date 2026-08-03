using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;

namespace Mimir.Server.Tests.Distillation;

/// <summary>The §6 sweep over <see cref="UnreachableStorage"/>, the sibling of
/// <see cref="DistillerServiceFailureTests"/>.</summary>
public sealed class DistillationSweepServiceFailureTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    private static readonly DistillationOptions SweepOptions = new();

    private readonly LoopClock _clock = new(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturedLog<DistillationSweepService> _log = new();
    private readonly ServiceProvider _provider;
    private readonly DistillationSweepService _service;

    public DistillationSweepServiceFailureTests()
    {
        var services = new ServiceCollection();
        UnreachableStorage.Add(services);
        services.AddScoped<DistillationSweep>();
        services.AddScoped<DistillationQueue>();
        services.AddScoped<ContestedSweep>();
        services.AddSingleton(Options.Create(SweepOptions));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();

        _service = new DistillationSweepService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new DistillationTrigger(),
            Options.Create(SweepOptions),
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
    public async Task ASweepThatCannotReachStorage_IsLogged_AndTheLoopSurvivesToTheNextInterval()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);

        await LoopWaits.UntilAsync(
            () => _log.Warnings.Count >= 1,
            "log its first failed sweep",
            Patience,
            TestContext.Current.CancellationToken);
        _log.Warnings[0].ShouldContain("Distillation sweep failed");

        _service.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse(
            "the sweep ends on a shutdown cancellation and on nothing else");

        await _clock.StraddleAsync(
            SweepOptions.SweepInterval,
            Margin,
            () => _log.Warnings.Count >= 2,
            Patience,
            TestContext.Current.CancellationToken);

        _service.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
    }
}
