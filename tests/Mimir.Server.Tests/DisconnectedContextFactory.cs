using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests;

/// <summary>
/// Hands out contexts pointed at a host that does not resolve. A guard test — one whose subject
/// returns before it ever issues SQL — builds its SUT over this rather than over
/// <see cref="PostgresTestBase"/>: deliberately outside the harness, so the guard runs, and fails,
/// on a machine with no Postgres instead of disappearing into a skip.
/// </summary>
public sealed class DisconnectedContextFactory : IDbContextFactory<MimirDbContext>
{
    public MimirDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<MimirDbContext>()
            .UseNpgsql("Host=guard-checks-never-connect")
            .Options);
}
