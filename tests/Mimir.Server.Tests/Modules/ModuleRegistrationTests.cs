using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Distillation;
using Mimir.Server.Evaluation;
using Mimir.Server.Modules;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Modules;

/// <summary>
/// What the one module list registers, read off the registry the app itself walks. Issues no SQL,
/// so it runs on a machine with no Docker.
/// </summary>
public sealed class ModuleRegistrationTests
{
    [Fact]
    public void TheMergeGate_IsASingletonTakingNoScopedState()
    {
        var gate = Registrations().Single(d => d.ServiceType == typeof(MergeGate));

        gate.Lifetime.ShouldBe(
            ServiceLifetime.Singleton, "the §8 curation surface outlives any request scope");
        typeof(MergeGate).GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ShouldNotContain(
                typeof(MimirDbContext),
                "a Singleton captures whatever it is handed: the gate opens its own context per batch");
    }

    [Fact]
    public void TheGoldenRunner_IsRegisteredNowhere()
        => Registrations().ShouldNotContain(
            d => d.ServiceType == typeof(GoldenRunner),
            "the §9 runner is dev-time only — the golden suite test is its one consumer");

    private static ServiceCollection Registrations()
    {
        var services = new ServiceCollection();
        services.AddMimirModules(new ConfigurationBuilder().Build());
        return services;
    }
}
