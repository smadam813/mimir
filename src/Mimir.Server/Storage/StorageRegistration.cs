using Microsoft.EntityFrameworkCore;

namespace Mimir.Server.Storage;

public static class StorageRegistration
{
    public const string ConnectionStringName = "Mimir";

    /// <summary>Spec §3 / ADR-0005: one Postgres for vectors, full-text and relational metadata.</summary>
    public static IServiceCollection AddMimirStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"No '{ConnectionStringName}' connection string is configured.");

        // Blazor circuits outlive any sensible DbContext lifetime, so they open a short-lived
        // context per interaction through the factory; the capture path and hosted services still
        // resolve the plain scoped MimirDbContext. AddDbContextFactory registers only the factory,
        // not the context itself, so register both.
        void Configure(DbContextOptionsBuilder options) =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector());

        services.AddDbContextFactory<MimirDbContext>(Configure);
        services.AddDbContext<MimirDbContext>(Configure);
        services.AddScoped<IStorageProbe, PostgresStorageProbe>();
        services.AddScoped<WisdomSearch>();
        services.AddHostedService<StorageService>();

        return services;
    }
}
