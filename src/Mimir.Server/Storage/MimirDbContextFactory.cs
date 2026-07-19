using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mimir.Server.Storage;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build a context without booting the whole server (and
/// therefore without a running Postgres). Design-time only — never used at runtime.
/// </summary>
internal sealed class MimirDbContextFactory : IDesignTimeDbContextFactory<MimirDbContext>
{
    public MimirDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Mimir")
            ?? "Host=localhost;Port=5432;Database=mimir;Username=mimir;Password=mimir";

        var options = new DbContextOptionsBuilder<MimirDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MimirDbContext(options);
    }
}
