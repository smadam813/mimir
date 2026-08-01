using Mimir.Contracts.Health;

namespace Mimir.Server.Storage;

internal static class StorageTileFactory
{
    public static StorageTile Ready(long databaseSizeBytes, IReadOnlyList<TableFootprint> tables) => new()
    {
        State = HealthTileState.Ready,
        Summary = $"{ByteSize.Format(databaseSizeBytes)} · {Describe(tables)}",
        DatabaseSizeBytes = databaseSizeBytes,
        Tables = tables,
    };

    public static StorageTile Unreachable(string error) => new()
    {
        State = HealthTileState.Degraded,
        Summary = $"Postgres unavailable — {error}",
    };

    private static string Describe(IReadOnlyList<TableFootprint> tables)
    {
        if (tables.Count == 0)
        {
            return "no tables yet";
        }

        var counted = $"{tables.Count} {(tables.Count == 1 ? "table" : "tables")}";

        if (tables.Any(table => table.Occupancy == TableOccupancy.Unknown))
        {
            return counted;
        }

        var empty = tables.Count(table => table.Occupancy == TableOccupancy.Empty);

        return empty switch
        {
            0 => counted,
            _ when empty == tables.Count => $"{counted}, all empty",
            _ => $"{counted}, {empty} empty",
        };
    }
}
