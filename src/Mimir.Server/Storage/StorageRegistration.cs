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

        services.AddDbContext<MimirDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IStorageProbe, PostgresStorageProbe>();
        services.AddHostedService<StorageService>();

        return services;
    }
}
