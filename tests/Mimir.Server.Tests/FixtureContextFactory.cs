using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests;

internal sealed class FixtureContextFactory(ThrowawayDatabaseFixture fixture)
    : IDbContextFactory<MimirDbContext>
{
    public MimirDbContext CreateDbContext()
    {
        if (fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(TestPostgres.SkipMessage(reason));
        }

        return fixture.CreateContext();
    }
}
