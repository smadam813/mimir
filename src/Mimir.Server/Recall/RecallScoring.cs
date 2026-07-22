using Mimir.Server.Configuration;

namespace Mimir.Server.Recall;

/// <summary>
/// The §7 ranking factors as pure arithmetic: the Brief's query-free score and the query
/// ranking's per-hit multiplier, sharing the recency factor.
/// </summary>
internal static class RecallScoring
{
    /// <summary>§7: <c>max(floor, 0.5^(days_since_last_confirmed / half_life))</c>.</summary>
    public static double Recency(
        DateTimeOffset lastConfirmedAt, DateTimeOffset now, RecallOptions options)
        => Math.Max(
            options.RecencyFloor,
            Math.Pow(0.5, (now - lastConfirmedAt).TotalDays / options.RecencyHalfLifeDays));

    /// <summary>
    /// §7: <c>brief_score = recency × salience × (1 + log₂(1 + reinforcement))</c> — no query
    /// exists at session start, so rank comes from the Wisdom's own record alone.
    /// </summary>
    public static double BriefScore(
        int reinforcement,
        bool salient,
        DateTimeOffset lastConfirmedAt,
        DateTimeOffset now,
        RecallOptions options)
        => Recency(lastConfirmedAt, now, options)
            * (salient ? options.SalienceBoost : 1.0)
            * (1 + Math.Log2(1 + reinforcement));

    /// <summary>
    /// §7 query ranking: <c>RRF(vector_rank, fts_rank) × affinity × recency × salience ×
    /// (1 + ln(1 + reinforcement)/10)</c>. The fused rank arrives from the hybrid search; the
    /// reinforcement damping is deliberately gentler than the Brief's — with a query in hand,
    /// relevance leads and confirmation count only nudges.
    /// </summary>
    /// <param name="projectAffinity">Whether the Wisdom's scope is the affinity context's own
    /// Project — never true for Global Wisdom (§7).</param>
    public static double QueryScore(
        double fusedScore,
        bool projectAffinity,
        int reinforcement,
        bool salient,
        DateTimeOffset lastConfirmedAt,
        DateTimeOffset now,
        RecallOptions options)
        => fusedScore
            * (projectAffinity ? options.AffinityBoost : 1.0)
            * Recency(lastConfirmedAt, now, options)
            * (salient ? options.SalienceBoost : 1.0)
            * (1 + (Math.Log(1 + reinforcement) / 10));
}
