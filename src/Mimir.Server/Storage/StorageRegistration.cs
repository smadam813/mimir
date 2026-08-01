using Microsoft.EntityFrameworkCore;

namespace Mimir.Server.Storage;

public static class StorageRegistration
{
    public const string ConnectionStringName = "Mimir";

    public static IServiceCollection AddMimirStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"No '{ConnectionStringName}' connection string is configured.");

        void Configure(DbContextOptionsBuilder options) =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector());

        services.AddDbContextFactory<MimirDbContext>(Configure);
        services.AddDbContext<MimirDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
        services.AddScoped<IStorageProbe, PostgresStorageProbe>();
        services.AddScoped<WisdomSearch>();
        services.AddScoped<EventSearch>();
        services.AddHostedService<StorageService>();

        return services;
    }
}
