using Microsoft.EntityFrameworkCore;
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

    /// <summary>The payload JSON clipped server-side to a preview — stored payloads run to tens
    /// of KB (prompts are whole, §4) and every consumer renders a snippet.</summary>
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
/// <c>ts_rank_cd</c> alone. Filters and rank are wholly in SQL with no cross-leg fusion, so the
/// caller's cap is the query's LIMIT — no over-fetch.
/// </summary>
public sealed class EventSearch(MimirDbContext db)
{
    /// <summary>Chars of payload text per hit — a margin over the caller's rendered snippet,
    /// which collapses whitespace before clipping.</summary>
    private const int PayloadPreviewChars = 1000;

    private const string Sql = """
        SELECT e.id AS "EventId",
               e.episode_id AS "EpisodeId",
               e.seq AS "Seq",
               e.type AS "Type",
               e.at AS "At",
               left(e.payload::text, @payload_chars) AS "Payload",
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
    /// <param name="topN">The caller's cap, applied as the query LIMIT.</param>
    public async Task<IReadOnlyList<EventSearchHit>> SearchAsync(
        string query, Guid? projectId, DateTimeOffset? since, int topN, CancellationToken cancellationToken)
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
                new NpgsqlParameter("top_n", topN),
                new NpgsqlParameter("payload_chars", PayloadPreviewChars))
            .ToListAsync(cancellationToken);
}
