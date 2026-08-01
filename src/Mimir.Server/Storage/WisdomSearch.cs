using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Mimir.Server.Storage;

public sealed record WisdomSearchFilter
{
    public static readonly WisdomSearchFilter None = new();

    public bool IncludeRetired { get; init; }

    public WisdomKind? Kind { get; init; }

    public Guid? ScopeProjectId { get; init; }

    public DateTimeOffset? Since { get; init; }
}

public sealed class WisdomSearchHit
{
    public Guid WisdomId { get; set; }

    public double FusedScore { get; set; }

    public double? Cosine { get; set; }
}

public sealed class WisdomSearch(MimirDbContext db, IOptions<SearchOptions> options)
{
    private const string AmbientClause = """
        (@ambient_project_id IS NULL
                  OR ((scope_project_id = @ambient_project_id OR scope_project_id = @global_id)
                      AND retired_at IS NULL
                      AND (NOT EXISTS (
                              SELECT 1 FROM provenance p WHERE p.wisdom_id = wisdom.id)
                          OR EXISTS (
                              SELECT 1 FROM provenance p
                              WHERE p.wisdom_id = wisdom.id
                                AND (p.harvested_item_id IS NULL
                                  OR NOT EXISTS (
                                      SELECT 1 FROM harvested_items h
                                      WHERE h.id = p.harvested_item_id
                                        AND h.project_id = @ambient_project_id))))))
        """;

    private const string Sql = $"""
        WITH vector_leg AS (
            SELECT id,
                   1 - (embedding <=> CAST(@embedding AS vector)) AS cosine,
                   row_number() OVER (ORDER BY embedding <=> CAST(@embedding AS vector), id) AS rank
            FROM wisdom
            WHERE (@include_retired OR retired_at IS NULL)
              AND (@kind IS NULL OR kind = @kind)
              AND (@scope_project_id IS NULL OR scope_project_id = @scope_project_id)
              AND (@since IS NULL OR last_confirmed_at >= @since)
              AND {AmbientClause}
            ORDER BY embedding <=> CAST(@embedding AS vector), id
            LIMIT @top_n
        ),
        fts_leg AS (
            SELECT id,
                   row_number() OVER (
                       ORDER BY ts_rank_cd(tsv, plainto_tsquery('english', @query)) DESC, id) AS rank
            FROM wisdom
            WHERE (@include_retired OR retired_at IS NULL)
              AND (@kind IS NULL OR kind = @kind)
              AND (@scope_project_id IS NULL OR scope_project_id = @scope_project_id)
              AND (@since IS NULL OR last_confirmed_at >= @since)
              AND {AmbientClause}
              AND tsv @@ plainto_tsquery('english', @query)
            ORDER BY ts_rank_cd(tsv, plainto_tsquery('english', @query)) DESC, id
            LIMIT @top_n
        )
        SELECT COALESCE(v.id, f.id) AS "WisdomId",
               CAST(COALESCE(1.0 / (@k + v.rank), 0)
                  + COALESCE(1.0 / (@k + f.rank), 0) AS double precision) AS "FusedScore",
               v.cosine AS "Cosine"
        FROM vector_leg v
        FULL JOIN fts_leg f ON f.id = v.id
        ORDER BY "FusedScore" DESC, "WisdomId"
        """;

    private const string AmbientIdsSql = $"""
        SELECT id AS "Value"
        FROM wisdom
        WHERE {AmbientClause}
        """;

    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding, string query, CancellationToken cancellationToken)
        => await SearchAsync(embedding, query, WisdomSearchFilter.None, cancellationToken);

    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding, string query, WisdomSearchFilter filter, CancellationToken cancellationToken)
        => await SearchAsync(embedding, query, filter, ambientProjectId: null, cancellationToken);

    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAmbientAsync(
        Vector embedding, string query, Guid projectId, CancellationToken cancellationToken)
        => await SearchAsync(embedding, query, WisdomSearchFilter.None, projectId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListAmbientAsync(
        Guid projectId, CancellationToken cancellationToken)
        => await db.Database
            .SqlQueryRaw<Guid>(
                AmbientIdsSql,
                new NpgsqlParameter("ambient_project_id", NpgsqlDbType.Uuid) { Value = projectId },
                new NpgsqlParameter("global_id", NpgsqlDbType.Uuid) { Value = Project.GlobalId })
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding,
        string query,
        WisdomSearchFilter filter,
        Guid? ambientProjectId,
        CancellationToken cancellationToken)
    {
        // Bound as text and CAST in SQL: Vector.ToString is the pgvector input syntax, so the
        // raw-SQL path needs no vector type mapping.
        var hits = await db.Database
            .SqlQueryRaw<WisdomSearchHit>(
                Sql,
                new NpgsqlParameter("embedding", embedding.ToString()),
                new NpgsqlParameter("query", query),
                new NpgsqlParameter("top_n", options.Value.PerLegTopN),
                new NpgsqlParameter("k", options.Value.RrfK),
                new NpgsqlParameter("include_retired", filter.IncludeRetired),
                new NpgsqlParameter("kind", NpgsqlDbType.Text)
                {
                    Value = (object?)filter.Kind?.ToString() ?? DBNull.Value,
                },
                new NpgsqlParameter("scope_project_id", NpgsqlDbType.Uuid)
                {
                    Value = (object?)filter.ScopeProjectId ?? DBNull.Value,
                },
                new NpgsqlParameter("since", NpgsqlDbType.TimestampTz)
                {
                    Value = (object?)filter.Since ?? DBNull.Value,
                },
                new NpgsqlParameter("ambient_project_id", NpgsqlDbType.Uuid)
                {
                    Value = (object?)ambientProjectId ?? DBNull.Value,
                },
                new NpgsqlParameter("global_id", NpgsqlDbType.Uuid)
                {
                    Value = Project.GlobalId,
                })
            .ToListAsync(cancellationToken);
        return hits;
    }
}
