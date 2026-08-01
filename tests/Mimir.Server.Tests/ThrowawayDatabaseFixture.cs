using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests;

public sealed class ThrowawayDatabaseFixture : IAsyncLifetime
{
    private readonly string _adminConnectionString = TestPostgres.AdminConnectionString;

    private readonly string _databaseName = $"mimir_test_{Guid.NewGuid():N}";

    public string? UnavailableReason { get; private set; }

    public string ConnectionString { get; private set; } = "";

    public Project? GlobalSeed { get; private set; }

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
