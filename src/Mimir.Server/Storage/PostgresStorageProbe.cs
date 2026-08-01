using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Mimir.Contracts.Health;

namespace Mimir.Server.Storage;

/// <inheritdoc cref="IStorageProbe"/>
internal sealed class PostgresStorageProbe(MimirDbContext context, ILogger<PostgresStorageProbe> logger)
    : IStorageProbe
{
    /// <summary>Postgres <c>undefined_table</c> — a table vanished between discovery and use.</summary>
    private const string UndefinedTable = "42P01";

    public async Task<StorageTile> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(cancellationToken);

            // No wrapping transaction, deliberately: Postgres sizes come from the filesystem and
            // ignore the snapshot (measured: 8 KB then 27 MB inside one), while EXISTS honours it,
            // so REPEATABLE READ here produces "27 MB and empty".
            var sizeBytes = Convert.ToInt64(
                await ScalarAsync(connection, StorageQueries.DatabaseSize, cancellationToken));
            var footprints = await ReadFootprintsAsync(connection, cancellationToken);
            var occupancy = await ReadOccupancyAsync(
                connection,
                footprints.Select(footprint => footprint.Table),
                cancellationToken);

            var tables = footprints
                .Select(footprint => new TableFootprint(
                    footprint.Table,
                    footprint.TotalBytes,
                    occupancy.GetValueOrDefault(footprint.Table, TableOccupancy.Unknown)))
                .ToArray();

            return StorageTileFactory.Ready(sizeBytes, tables);
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

    private static async Task<IReadOnlyList<(string Table, long TotalBytes)>> ReadFootprintsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = StorageQueries.TableFootprints;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var footprints = new List<(string, long)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            // Postgres sizes a table mid-drop as NULL rather than erroring (measured), and
            // GetInt64 on NULL throws an InvalidCastException no DbException filter would catch.
            if (await reader.IsDBNullAsync(1, cancellationToken))
            {
                continue;
            }

            footprints.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return footprints;
    }

    private async Task<Dictionary<string, TableOccupancy>> ReadOccupancyAsync(
        DbConnection connection,
        IEnumerable<string> tables,
        CancellationToken cancellationToken)
    {
        if (StorageQueries.Occupancy(tables) is not { } sql)
        {
            return [];
        }

        var occupancy = new Dictionary<string, TableOccupancy>(StringComparer.Ordinal);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                occupancy[reader.GetString(0)] = reader.GetBoolean(1)
                    ? TableOccupancy.Populated
                    : TableOccupancy.Empty;
            }
        }
        catch (DbException ex) when (ex.SqlState == UndefinedTable)
        {
            // Swallowed: the union aborts as a whole, so nothing is known about any table, and
            // Unknown is the only answer that is not a guess.
            logger.LogDebug(ex, "A table vanished mid-probe; reporting occupancy as unknown this round");
            return [];
        }

        return occupancy;
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
