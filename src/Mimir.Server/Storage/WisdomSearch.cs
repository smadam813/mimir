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
/// exactly this split. Serves the Merge Gate and the §7 recall lanes.
/// </summary>
/// <remarks>
/// The Candidate Universe is named by the method, never assembled by the caller: <see
/// cref="SearchAmbientAsync"/> and <see cref="ListAmbientAsync"/> are the ambient universe (with
/// and without a query), <see cref="SearchAsync(Vector, string, WisdomSearchFilter,
/// CancellationToken)"/> is everything, narrowed only by the filter. Storage owns the universe, so
/// no combination of filter properties can contradict it and no lane can forget it.
/// </remarks>
public sealed class WisdomSearch(MimirDbContext db, IOptions<SearchOptions> options)
{
    /// <summary>
    /// The ambient Candidate Universe (§7) in SQL — the only implementation of the rule, shared
    /// verbatim by both search legs and by the queryless listing so it cannot drift into two
    /// rules. Self-contained, carrying all three of the universe's predicates: scope is the
    /// session's Project or Global, non-Retired (making the search's <c>@include_retired</c>
    /// guard a redundancy here, not the rule's only keeper), minus the native-content exclusion —
    /// Wisdom whose only Provenance is HarvestedItems of the session's Project never surfaces
    /// ambiently; orphaned provenance is not harvest-only, so it stays in. The null-parameter
    /// escape is how <see cref="Sql"/> serves the everything universe off the same text; the
    /// queryless listing always binds a Project, so for it the escape never fires.
    /// </summary>
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

    /// <remarks>
    /// No LIMIT and no ordering: the ambient lanes that have no query rank the whole universe
    /// themselves, so truncating or ordering here would be a second, silent ranking.
    /// </remarks>
    private const string AmbientIdsSql = $"""
        SELECT id AS "Value"
        FROM wisdom
        WHERE {AmbientClause}
        """;

    /// <param name="embedding">The query embedding (qwen3-embedding:0.6b, 1024 dims).</param>
    /// <param name="query">The query text, for the FTS leg.</param>
    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding, string query, CancellationToken cancellationToken)
        => await SearchAsync(embedding, query, WisdomSearchFilter.None, cancellationToken);

    /// <summary>The everything universe: every Project's Wisdom, narrowed only by
    /// <paramref name="filter"/>.</summary>
    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding, string query, WisdomSearchFilter filter, CancellationToken cancellationToken)
        => await SearchAsync(embedding, query, filter, ambientProjectId: null, cancellationToken);

    /// <summary>
    /// The ambient Candidate Universe (§7) of <paramref name="projectId"/> as a search mode: both
    /// legs restrict to it <em>before</em> their per-leg LIMIT, so a nearer foreign corpus can
    /// never crowd an eligible match out of the pool.
    /// </summary>
    public async Task<IReadOnlyList<WisdomSearchHit>> SearchAmbientAsync(
        Vector embedding, string query, Guid projectId, CancellationToken cancellationToken)
        => await SearchAsync(embedding, query, WisdomSearchFilter.None, projectId, cancellationToken);

    /// <summary>
    /// The same universe with no query: every Wisdom id inside it, for the lanes that rank without
    /// a search (§7's Brief). Unordered and unlimited — the caller ranks, and hydrates the ids it
    /// keeps.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListAmbientAsync(
        Guid projectId, CancellationToken cancellationToken)
        => await db.Database
            .SqlQueryRaw<Guid>(
                AmbientIdsSql,
                new NpgsqlParameter("ambient_project_id", NpgsqlDbType.Uuid) { Value = projectId },
                new NpgsqlParameter("global_id", NpgsqlDbType.Uuid) { Value = Project.GlobalId })
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Both universes over the one SQL text. The two parameters are alternatives, not a
    /// combination — every public entry point above passes a constant for one of them, and the
    /// SQL simply ANDs whatever it is given: an ambient search told to include Retired would
    /// silently exclude it anyway, one given a scope would silently intersect. That is why the
    /// choice stays here, between two visible call sites, instead of on a filter a caller fills
    /// in — no runtime check can be forgotten if no caller can state the contradiction.
    /// </summary>
    private async Task<IReadOnlyList<WisdomSearchHit>> SearchAsync(
        Vector embedding,
        string query,
        WisdomSearchFilter filter,
        Guid? ambientProjectId,
        CancellationToken cancellationToken)
    {
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
