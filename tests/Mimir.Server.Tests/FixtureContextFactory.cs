using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests;

/// <summary>
/// Adapts the throwaway-database fixture to the <see cref="IDbContextFactory{TContext}"/> the Ui
/// browsers take, skipping like every other Postgres-backed path when no database is reachable.
/// </summary>
internal sealed class FixtureContextFactory(CaptureDatabaseFixture fixture) : IDbContextFactory<MimirDbContext>
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
