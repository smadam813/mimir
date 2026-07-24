using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Mimir.Server.Storage;

/// <summary>
/// Optional narrowing of the §3 search, applied in SQL <em>before</em> the per-leg LIMIT — a
/// filtered search ranks the best matching rows of the whole corpus, never the filtered residue
/// of an unfiltered top-N (which can be empty while matches exist deeper).
/// </summary>
public sealed record WisdomSearchFilter
{
    public static readonly WisdomSearchFilter None = new();

    /// <summary>§7: Retired Wisdom surfaces only for <c>mimir_search</c> with
    /// <c>include_retired</c>; every other caller keeps "Retired never ranks".</summary>
    public bool IncludeRetired { get; init; }

    public WisdomKind? Kind { get; init; }

    public Guid? ScopeProjectId { get; init; }

    /// <summary>Keep only Wisdom confirmed at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>
    /// §7: restrict both legs to the ambient Candidate Universe of this session Project — the
    /// Project plus Global, non-Retired, minus the native-content exclusion. The EF form in
    /// <c>AmbientCandidates</c> owns the rule; a parity test pins the SQL here to it. Combining
    /// with <see cref="IncludeRetired"/> or <see cref="ScopeProjectId"/> contradicts the
    /// universe's definition and is rejected at the search seam.
    /// </summary>
    public Guid? AmbientProjectId { get; init; }
}

/// <summary>
/// One fused row of the hybrid search. <see cref="FusedScore"/> is a rank-fusion value (max ≈
/// 0.033) for ordering only; <see cref="Cosine"/> is the vector leg's cosine similarity, null for
/// rows the FTS leg alone surfaced — and per the §3 score-scale rule it is the only number a
/// threshold may ever be compared against.
/// </summary>
public sealed class WisdomSearchHit
{
    public Guid WisdomId { get; set; }

    public double FusedScore { get; set; }

    public double? Cosine { get; set; }
}

/// <summary>
/// The §3 hybrid search over non-Retired Wisdom: pgvector cosine KNN + tsvector FTS, top
/// <see cref="SearchOptions.PerLegTopN"/> per leg, fused with RRF (k = <see cref="SearchOptions.RrfK"/>)
/// in hand-written SQL — EF Core cannot express window-ranked fusion, and ADR-0005 plans for
/// exactly this split. Serves the Merge Gate now and the §7 recall lanes later.
/// </summary>
public sealed class WisdomSearch(MimirDbContext db, IOptions<SearchOptions> options)
{
    /// <summary>
    /// The ambient Candidate Universe (§7) in SQL, shared verbatim by both legs so the rule
    /// cannot drift into two rules: scope is the session's Project or Global, minus the
    /// native-content exclusion — Wisdom whose only Provenance is HarvestedItems of the session's
    /// Project never ranks ambiently; orphaned provenance is not harvest-only, so it stays in.
    /// <c>AmbientCandidates</c> owns the EF form; a parity test pins this clause to it.
    /// </summary>
    private const string AmbientClause = """
        (@ambient_project_id IS NULL
                  OR ((scope_project_id = @ambient_project_id OR scope_project_id = @global_id)
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

    /// <remarks>
    /// Each leg ranks within itself (row_number over its own order) and contributes 1/(k+rank);
    /// a FULL JOIN keeps rows that only one leg surfaced. Ties break on id so the ordering is
    /// deterministic under equal scores. The vector leg's cosine rides along unfused.
    /// </remarks>
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

    /// <param name="embedding">The query embedding (qwen3-embedding:0.6b, 1024 dims).</param>
    /// <param name="query">The query text, for the FTS leg.</param>
    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding, string query, CancellationToken cancellationToken)
        => await SearchAsync(embedding, query, WisdomSearchFilter.None, cancellationToken);

    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding, string query, WisdomSearchFilter filter, CancellationToken cancellationToken)
    {
        if (filter.AmbientProjectId is not null)
        {
            if (filter.IncludeRetired)
            {
                throw new ArgumentException(
                    "The ambient Candidate Universe never ranks Retired Wisdom; " +
                    $"{nameof(filter.IncludeRetired)} contradicts {nameof(filter.AmbientProjectId)}.",
                    nameof(filter));
            }

            if (filter.ScopeProjectId is not null)
            {
                throw new ArgumentException(
                    "The ambient Candidate Universe fixes its own scope (the session's Project " +
                    $"plus Global); {nameof(filter.ScopeProjectId)} contradicts " +
                    $"{nameof(filter.AmbientProjectId)}.",
                    nameof(filter));
            }
        }

        // The vector arrives as its text form and is cast in SQL, so the query needs no vector
        // type mapping on the raw-SQL path (Vector.ToString is the pgvector input syntax).
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
                    Value = (object?)filter.AmbientProjectId ?? DBNull.Value,
                },
                new NpgsqlParameter("global_id", NpgsqlDbType.Uuid)
                {
                    Value = Project.GlobalId,
                })
            .ToListAsync(cancellationToken);
        return hits;
    }
}
