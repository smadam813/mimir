using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Mimir.Contracts.Health;

namespace Mimir.Server.Storage;

/// <inheritdoc cref="IStorageProbe"/>
internal sealed class PostgresStorageProbe(MimirDbContext context, ILogger<PostgresStorageProbe> logger)
    : IStorageProbe
{
    public async Task<StorageTile> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(cancellationToken);

            var sizeBytes = Convert.ToInt64(await ScalarAsync(connection, StorageQueries.DatabaseSize, cancellationToken));
            var tables = await ReadTableNamesAsync(connection, cancellationToken);

            return StorageTileFactory.Ready(sizeBytes, await ReadRowCountsAsync(connection, tables, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Storage probe failed");
            return StorageTileFactory.Unreachable(ex.Message);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = StorageQueries.PublicTables;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<IReadOnlyList<TableRowCount>> ReadRowCountsAsync(
        DbConnection connection,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        if (StorageQueries.RowCounts(tables) is not { } sql)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new List<TableRowCount>();
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new TableRowCount(reader.GetString(0), reader.GetInt64(1)));
        }

        return counts;
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
