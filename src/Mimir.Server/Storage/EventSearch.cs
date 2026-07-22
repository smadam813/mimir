using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Mimir.Server.Storage;

/// <summary>
/// One matching Event of the Episode leg, carrying enough of its Episode to render a timeline
/// entry without a second query. <c>Type</c> arrives as the stored enum string.
/// </summary>
public sealed class EventSearchHit
{
    public Guid EventId { get; set; }

    public Guid EpisodeId { get; set; }

    public int Seq { get; set; }

    public required string Type { get; set; }

    public DateTimeOffset At { get; set; }

    public required string Payload { get; set; }

    public required string SessionId { get; set; }

    public Guid ProjectId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? SealedAt { get; set; }

    public string? SealReason { get; set; }
}

/// <summary>
/// The Episode leg of <c>mimir_search</c> (§7): FTS-only over <c>Event.tsv</c> plus metadata
/// filters — Events carry no embeddings in v1, so there is nothing to fuse with and rank is
/// <c>ts_rank_cd</c> alone. Bounded per leg like the §3 hybrid search.
/// </summary>
public sealed class EventSearch(MimirDbContext db, IOptions<SearchOptions> options)
{
    private const string Sql = """
        SELECT e.id AS "EventId",
               e.episode_id AS "EpisodeId",
               e.seq AS "Seq",
               e.type AS "Type",
               e.at AS "At",
               e.payload::text AS "Payload",
               ep.session_id AS "SessionId",
               ep.project_id AS "ProjectId",
               ep.started_at AS "StartedAt",
               ep.sealed_at AS "SealedAt",
               ep.seal_reason AS "SealReason"
        FROM events e
        JOIN episodes ep ON ep.id = e.episode_id
        WHERE e.tsv @@ plainto_tsquery('english', @query)
          AND (@project_id IS NULL OR ep.project_id = @project_id)
          AND (@since IS NULL OR e.at >= @since)
        ORDER BY ts_rank_cd(e.tsv, plainto_tsquery('english', @query)) DESC, e.id
        LIMIT @top_n
        """;

    /// <param name="projectId">Narrow to one Project's Episodes; null reaches every Project.</param>
    /// <param name="since">Keep only Events captured at or after this instant.</param>
    public async Task<IReadOnlyList<EventSearchHit>> SearchAsync(
        string query, Guid? projectId, DateTimeOffset? since, CancellationToken cancellationToken)
        => await db.Database
            .SqlQueryRaw<EventSearchHit>(
                Sql,
                new NpgsqlParameter("query", query),
                new NpgsqlParameter("project_id", NpgsqlDbType.Uuid)
                {
                    Value = (object?)projectId ?? DBNull.Value,
                },
                new NpgsqlParameter("since", NpgsqlDbType.TimestampTz)
                {
                    Value = (object?)since ?? DBNull.Value,
                },
                new NpgsqlParameter("top_n", options.Value.PerLegTopN))
            .ToListAsync(cancellationToken);
}
