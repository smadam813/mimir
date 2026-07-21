using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Npgsql;
using Pgvector;

namespace Mimir.Server.Storage;

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
    /// <remarks>
    /// Each leg ranks within itself (row_number over its own order) and contributes 1/(k+rank);
    /// a FULL JOIN keeps rows that only one leg surfaced. Ties break on id so the ordering is
    /// deterministic under equal scores. The vector leg's cosine rides along unfused.
    /// </remarks>
    private const string Sql = """
        WITH vector_leg AS (
            SELECT id,
                   1 - (embedding <=> CAST(@embedding AS vector)) AS cosine,
                   row_number() OVER (ORDER BY embedding <=> CAST(@embedding AS vector), id) AS rank
            FROM wisdom
            WHERE retired_at IS NULL
            ORDER BY embedding <=> CAST(@embedding AS vector), id
            LIMIT @top_n
        ),
        fts_leg AS (
            SELECT id,
                   row_number() OVER (
                       ORDER BY ts_rank_cd(tsv, plainto_tsquery('english', @query)) DESC, id) AS rank
            FROM wisdom
            WHERE retired_at IS NULL
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
    {
        // The vector arrives as its text form and is cast in SQL, so the query needs no vector
        // type mapping on the raw-SQL path (Vector.ToString is the pgvector input syntax).
        var hits = await db.Database
            .SqlQueryRaw<WisdomSearchHit>(
                Sql,
                new NpgsqlParameter("embedding", embedding.ToString()),
                new NpgsqlParameter("query", query),
                new NpgsqlParameter("top_n", options.Value.PerLegTopN),
                new NpgsqlParameter("k", options.Value.RrfK))
            .ToListAsync(cancellationToken);
        return hits;
    }
}
