using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests;

/// <summary>
/// A migrated, throwaway database per test class: created on first use, dropped on dispose, so
/// tests never leave rows in the development database. Skips (via <see cref="UnavailableReason"/>)
/// when no Postgres is reachable; <c>docker compose up -d postgres</c> turns them on. Named for
/// what it is rather than its first consumer — every Postgres-backed class in the suite reaches it
/// through <see cref="PostgresTestBase"/>.
/// </summary>
public sealed class ThrowawayDatabaseFixture : IAsyncLifetime
{
    private readonly string _adminConnectionString = TestPostgres.AdminConnectionString;

    private readonly string _databaseName = $"mimir_test_{Guid.NewGuid():N}";

    /// <summary>Why the database is unusable, or null when it is usable.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Connection string to the migrated throwaway database.</summary>
    public string ConnectionString { get; private set; } = "";

    /// <summary>
    /// The §3 Global pseudo-project exactly as the migration's <c>HasData</c> left it, read once
    /// before any test could touch it. The per-test reset truncates it away with everything else
    /// and restores a copy of this; reading it fresh each time would instead carry a test's
    /// mutation of that row into every later test in the class. Still migration-sourced, so
    /// dropping the seed leaves this null and the harness's own pin goes red rather than passing
    /// against a hand-built stand-in.
    /// </summary>
    public Project? GlobalSeed { get; private set; }

    /// <summary>A context on the throwaway database. Callers dispose it.</summary>
    public MimirDbContext CreateContext()
        => new(new DbContextOptionsBuilder<MimirDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .Options);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await ExecuteOnAdminAsync($"CREATE DATABASE \"{_databaseName}\"");

            ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
            {
                Database = _databaseName,
            }.ConnectionString;

            await using var context = CreateContext();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            GlobalSeed = await context.Projects
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    project => project.Id == Project.GlobalId, TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (UnavailableReason is not null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await ExecuteOnAdminAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
    }

    private async Task ExecuteOnAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
