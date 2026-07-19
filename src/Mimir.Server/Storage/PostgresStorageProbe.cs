using System.Data;
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

            // One snapshot, so the sizes and the occupancy answers describe the same moment and
            // cannot contradict each other. It deliberately does NOT claim to stop a table
            // vanishing mid-probe: below SERIALIZABLE, Postgres resolves relation names against a
            // fresh catalog snapshot, so a dropped table still raises 42P01 in the occupancy query
            // (measured). That race is handled where it lands, in ReadOccupancyAsync.
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var sizeBytes = Convert.ToInt64(
                await ScalarAsync(connection, transaction, StorageQueries.DatabaseSize, cancellationToken));
            var footprints = await ReadFootprintsAsync(connection, transaction, cancellationToken);
            var occupancy = await ReadOccupancyAsync(
                connection,
                transaction,
                footprints.Select(footprint => footprint.Table),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

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

    /// <summary>Every reportable table with its total on-disk bytes, in catalog order.</summary>
    private static async Task<IReadOnlyList<(string Table, long TotalBytes)>> ReadFootprintsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StorageQueries.TableFootprints;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var footprints = new List<(string, long)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            // A table dropped since this transaction's snapshot is still listed by the pg_class
            // scan, but sizing it yields NULL rather than an error (measured). Leave it out: it no
            // longer exists, and naming it in the occupancy query would only earn a 42P01.
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
        DbTransaction transaction,
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
            command.Transaction = transaction;
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
            // A table went away underneath us. The union aborts as a whole, so we know nothing
            // about any of them — and "unknown" is the only answer that is not a guess. Reporting
            // sizes with no occupancy beats flagging the whole tile Degraded over a routine race.
            logger.LogDebug(ex, "A table vanished mid-probe; reporting occupancy as unknown this round");
            return [];
        }

        return occupancy;
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
