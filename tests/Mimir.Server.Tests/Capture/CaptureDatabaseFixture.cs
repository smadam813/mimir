using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// A migrated, throwaway database per test class: created on first use, dropped on dispose, so
/// capture tests never leave rows in the development database. Skips (via
/// <see cref="UnavailableReason"/>) when no Postgres is reachable, same as the storage tests;
/// <c>docker compose up -d postgres</c> turns them on.
/// </summary>
public sealed class CaptureDatabaseFixture : IAsyncLifetime
{
    private readonly string _adminConnectionString = TestPostgres.AdminConnectionString;

    private readonly string _databaseName = $"mimir_test_{Guid.NewGuid():N}";

    /// <summary>Why the database is unusable, or null when it is usable.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Connection string to the migrated throwaway database.</summary>
    public string ConnectionString { get; private set; } = "";

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
