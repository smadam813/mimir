using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests;

/// <summary>
/// Adapts the throwaway-database fixture to the <see cref="IDbContextFactory{TContext}"/> the Ui
/// browsers and the Merge Gate take, skipping like every other Postgres-backed path when no
/// database is reachable.
/// </summary>
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
