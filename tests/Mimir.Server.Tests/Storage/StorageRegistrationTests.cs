using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// What <c>AddMimirStorage</c> puts in the container. No Postgres: nothing here opens a
/// connection, and a graph this shape is exactly what a machine without Docker should still be
/// able to catch.
/// </summary>
public sealed class StorageRegistrationTests
{
    [Fact]
    public void BothTheFactoryAndThePlainScopedContext_AreRegistered()
    {
        using var provider = Compose();

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<IDbContextFactory<MimirDbContext>>().ShouldNotBeNull(
            "the Blazor circuits open a short-lived context per interaction through the factory");
        scope.ServiceProvider.GetService<MimirDbContext>().ShouldNotBeNull(
            "AddDbContextFactory registers only the factory, so the capture path's scoped context "
            + "has to be registered alongside it");
    }

    [Fact]
    public void TheContextOptions_StaySingletonAlongsideTheFactory()
    {
        var services = new ServiceCollection().AddLogging().AddMimirStorage(Configuration());

        services.Single(d => d.ServiceType == typeof(DbContextOptions<MimirDbContext>))
            .Lifetime.ShouldBe(
                ServiceLifetime.Singleton,
                "Scoped options would poison the singleton factory's root-provider resolution (#23)");
    }

    [Fact]
    public void TheSingletonFactory_ResolvesFromTheRootProvider()
    {
        using var provider = Compose();

        Should.NotThrow(() => provider.GetRequiredService<IDbContextFactory<MimirDbContext>>());
    }

    [Fact]
    public void AMissingConnectionString_IsRefusedAtComposition()
    {
        var services = new ServiceCollection();

        var failure = Should.Throw<InvalidOperationException>(
            () => services.AddMimirStorage(new ConfigurationBuilder().Build()));

        failure.Message.ShouldContain(StorageRegistration.ConnectionStringName);
    }

    private static ServiceProvider Compose()
        => new ServiceCollection()
            .AddLogging()
            .AddMimirStorage(Configuration())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static IConfiguration Configuration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{StorageRegistration.ConnectionStringName}"] =
                    "Host=registration-checks-never-connect;Database=nope;Username=nobody;Password=nobody",
            })
            .Build();
}
