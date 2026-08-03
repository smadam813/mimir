using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests;

/// <summary>
/// Storage that answers nothing, for the classes whose subject is what runs while Postgres is
/// <em>not</em> there. Stands apart from <see cref="PostgresTestBase"/> rather than extending it,
/// so a class built on this skips nowhere.
/// </summary>
internal static class UnreachableStorage
{
    // A unix-socket directory that does not exist: the connection fails immediately on every
    // platform, so a retry loop is observable without paying a TCP timeout per turn.
    private const string ConnectionString =
        "Host=/mimir-tests-no-such-socket-dir;Username=nobody;Password=nobody;Database=nope";

    /// <summary>Both context registrations <c>AddMimirStorage</c> makes, and nothing else.</summary>
    public static void Add(IServiceCollection services)
    {
        void Configure(DbContextOptionsBuilder options) =>
            options.UseNpgsql(ConnectionString, npgsql => npgsql.UseVector());
        services.AddDbContextFactory<MimirDbContext>(Configure);
        services.AddDbContext<MimirDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
    }
}
