using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;

namespace Mimir.Server.Capture;

/// <summary>
/// Spec §3.1 clone merge: re-point every reference to the loser at the survivor, union
/// <c>root_paths</c>, remove the loser — one transaction, so a crash leaves both rows intact.
/// References are enumerated from the database catalog at merge time, not a hand-list of today's
/// tables: HarvestedItem, Wisdom scope, Injection and GoldenCase arrive with later tickets and
/// must be re-pointed without edits here.
/// </summary>
internal static class ProjectMerger
{
    public static async Task MergeAsync(
        MimirDbContext db,
        Guid survivorId,
        Guid loserId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var reference in await ReferencesToProjectsAsync(db, cancellationToken))
        {
            // The identifiers cannot be parameters; both arrive quoted by the catalog itself
            // (regclass::text and quote_ident below), so raw interpolation is injection-safe here.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE {reference.Table} SET {reference.Column} = {{0}} WHERE {reference.Column} = {{1}}",
                [survivorId, loserId],
                cancellationToken);
#pragma warning restore EF1002
        }

        // Union preserving order: the survivor's roots stay put, the loser's unseen roots append
        // in the order the loser accumulated them.
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE projects AS s
            SET root_paths = s.root_paths || (
                SELECT COALESCE(array_agg(u.root ORDER BY u.ord), ARRAY[]::text[])
                FROM unnest((SELECT l.root_paths FROM projects AS l WHERE l.id = {loserId}))
                    WITH ORDINALITY AS u(root, ord)
                WHERE NOT (u.root = ANY (s.root_paths))
            )
            WHERE s.id = {survivorId}
            """,
            cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM projects WHERE id = {loserId}",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Every foreign key in the database that references <c>projects</c>, straight from the
    /// catalog — a table added tomorrow is covered the moment its constraint exists. Each must be
    /// a single column referencing <c>projects.id</c>; any other shape is unmergeable and must
    /// fail the merge loudly, not silently strand rows.
    /// </summary>
    private static async Task<List<ProjectReference>> ReferencesToProjectsAsync(
        MimirDbContext db,
        CancellationToken cancellationToken)
    {
        var references = await db.Database.SqlQuery<ProjectReference>(
            $"""
            SELECT con.conrelid::regclass::text AS "Table",
                   quote_ident(att.attname) AS "Column",
                   ref.attname::text AS "ReferencedColumn",
                   cardinality(con.conkey) AS "Width"
            FROM pg_constraint con
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = con.conkey[1]
            JOIN pg_attribute ref ON ref.attrelid = con.confrelid AND ref.attnum = con.confkey[1]
            WHERE con.contype = 'f' AND con.confrelid = 'projects'::regclass
            """).ToListAsync(cancellationToken);

        var unmergeable = references.FirstOrDefault(r => r.Width != 1 || r.ReferencedColumn != "id");
        if (unmergeable is not null)
        {
            throw new InvalidOperationException(
                $"Cannot merge Projects: {unmergeable.Table}.{unmergeable.Column} references "
                + $"projects.{unmergeable.ReferencedColumn} (width {unmergeable.Width}); only "
                + "single-column references to projects.id can be re-pointed.");
        }

        return references;
    }

    private sealed class ProjectReference
    {
        /// <summary>Referencing table, already quoted for SQL by <c>regclass::text</c>.</summary>
        public required string Table { get; set; }

        /// <summary>Referencing column, already quoted for SQL by <c>quote_ident</c>.</summary>
        public required string Column { get; set; }

        public required string ReferencedColumn { get; set; }

        public int Width { get; set; }
    }
}
