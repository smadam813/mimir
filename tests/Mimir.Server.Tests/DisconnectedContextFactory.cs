using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests;

public sealed class DisconnectedContextFactory : IDbContextFactory<MimirDbContext>
{
    public MimirDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<MimirDbContext>()
            .UseNpgsql("Host=guard-checks-never-connect")
            .Options);
}
