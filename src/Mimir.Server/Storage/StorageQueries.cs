namespace Mimir.Server.Storage;

/// <summary>
/// SQL for the spec §8 Storage tile. Deliberately schema-agnostic: it discovers whatever tables
/// exist rather than naming domain tables, so later tickets light it up without touching it.
/// </summary>
/// <remarks>
/// Per ADR-0006 the tile reports bytes and occupancy, never a row count. Bytes come from the
/// catalog and are always exact; occupancy comes from <c>EXISTS</c>, which reads live tuples
/// through MVCC and so cannot disagree with a count in the same snapshot. Neither is a statistic,
/// which is what keeps the tile from misreporting an empty database as populated or vice versa.
/// </remarks>
internal static class StorageQueries
{
    /// <summary>EF's own bookkeeping is infrastructure, not data worth reporting.</summary>
    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public const string DatabaseSize = "SELECT pg_database_size(current_database());";

    /// <summary>
    /// Discovery and sizing in one catalog read — no heap access, no dynamic SQL.
    /// </summary>
    /// <remarks>
    /// Two load-bearing details. <c>NOT relispartition</c> includes partitioned parents and
    /// excludes their children, where <c>pg_tables</c> returns both and double-counts them. And the
    /// <c>CASE</c> is mandatory rather than defensive: <c>pg_partition_tree</c> returns zero rows
    /// for an ordinary table, so an unconditional rollup would size every plain table at 0 bytes.
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

    /// <summary>
    /// One <c>EXISTS</c> leg per table, unioned. Returns null when there is nothing to ask about —
    /// the state of a freshly migrated database, before any domain table exists.
    /// </summary>
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

    /// <remarks>
    /// These names come from the catalog rather than from a user, but quoting them is what makes
    /// that provenance irrelevant — and it is also what lets mixed-case names resolve.
    /// </remarks>
    private static string Identifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
