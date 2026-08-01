using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mimir.Server.Storage;

/// <summary>Design-time only, never resolved at runtime: it exists so
/// <c>dotnet ef migrations add</c> needs no running server or Postgres.</summary>
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
