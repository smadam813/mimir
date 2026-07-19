using Mimir.Contracts.Health;

namespace Mimir.Server.Storage;

/// <summary>Turns a probe result into the spec §8 Storage tile face.</summary>
internal static class StorageTileFactory
{
    public static StorageTile Ready(long databaseSizeBytes, IReadOnlyList<TableRowCount> tables)
    {
        var contents = tables.Count == 0
            ? "no tables yet"
            : $"{tables.Count} tables, {tables.Sum(t => t.Rows):N0} rows";

        return new StorageTile
        {
            State = HealthTileState.Ready,
            Summary = $"{ByteSize.Format(databaseSizeBytes)} · {contents}",
            DatabaseSizeBytes = databaseSizeBytes,
            Tables = tables,
        };
    }

    public static StorageTile Unreachable(string error) => new()
    {
        State = HealthTileState.Degraded,
        Summary = $"Postgres unavailable — {error}",
    };
}
