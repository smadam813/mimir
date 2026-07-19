namespace Mimir.Server.Storage;

/// <summary>
/// SQL for the spec §8 Storage tile. Deliberately schema-agnostic: it discovers whatever tables
/// exist rather than naming domain tables, so later tickets light it up without touching it.
/// </summary>
internal static class StorageQueries
{
    /// <summary>EF's own bookkeeping is infrastructure, not data worth reporting.</summary>
    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public const string DatabaseSize = "SELECT pg_database_size(current_database());";

    public const string PublicTables =
        "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;";

    /// <summary>
    /// One <c>count(*)</c> leg per table, unioned. Returns null when there is nothing to count —
    /// the state of a freshly migrated database, before any domain table exists.
    /// </summary>
    public static string? RowCounts(IEnumerable<string> tables)
    {
        var legs = tables
            .Where(table => table != MigrationsHistoryTable)
            .Select(table => $"SELECT {Literal(table)} AS table_name, count(*) AS row_count FROM {Identifier(table)}")
            .ToArray();

        return legs.Length == 0
            ? null
            : string.Join("\nUNION ALL\n", legs) + "\nORDER BY table_name;";
    }

    /// <remarks>
    /// These names come from <c>pg_tables</c> rather than from a user, but quoting them is what
    /// makes that provenance irrelevant — and it is also what lets mixed-case names resolve.
    /// </remarks>
    private static string Identifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
