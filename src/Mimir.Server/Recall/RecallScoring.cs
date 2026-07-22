using Mimir.Server.Configuration;

namespace Mimir.Server.Recall;

/// <summary>
/// The §7 ranking factors as pure arithmetic. The Brief's query-free score lives here; the recency
/// factor is shared with the Prompt lane's query formula when its ticket arrives.
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
}
