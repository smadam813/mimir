namespace Mimir.Server.Storage;

internal static class StorageQueries
{
    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public const string DatabaseSize = "SELECT pg_database_size(current_database());";

    /// <remarks>
    /// The <c>CASE</c> is mandatory rather than defensive: <c>pg_partition_tree</c> returns zero
    /// rows for an ordinary table, so an unconditional rollup would size every plain table at 0.
    /// </remarks>
    public const string TableFootprints = """
        SELECT c.relname AS table_name,
               CASE WHEN c.relkind = 'p'
                    THEN (SELECT COALESCE(sum(pg_total_relation_size(p.relid)), 0)
                            FROM pg_partition_tree(c.oid) p WHERE p.isleaf)
                    ELSE pg_total_relation_size(c.oid)
               END AS total_bytes
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relkind IN ('r', 'p')
          AND NOT c.relispartition
          AND c.relname <> '__EFMigrationsHistory'
        ORDER BY c.relname;
        """;

    public static string? Occupancy(IEnumerable<string> tables)
    {
        var legs = tables
            .Where(table => table != MigrationsHistoryTable)
            .Select(table =>
                $"SELECT {Literal(table)} AS table_name, EXISTS(SELECT 1 FROM {Identifier(table)}) AS populated")
            .ToArray();

        return legs.Length == 0
            ? null
            : string.Join("\nUNION ALL\n", legs) + "\nORDER BY table_name;";
    }

    private static string Identifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
