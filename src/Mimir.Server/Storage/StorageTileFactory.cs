using Mimir.Contracts.Health;

namespace Mimir.Server.Storage;

/// <summary>Turns a probe result into the spec §8 Storage tile face.</summary>
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

        // With any occupancy in doubt, the honest summary is the table count alone. Folding an
        // Unknown in with the Populated ones would be the very misreport the enum exists to prevent.
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
