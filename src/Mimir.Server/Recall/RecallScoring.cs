using Mimir.Server.Configuration;

namespace Mimir.Server.Recall;

internal static class RecallScoring
{
    public static double Recency(
        DateTimeOffset lastConfirmedAt, DateTimeOffset now, RecallOptions options)
        => Math.Max(
            options.RecencyFloor,
            Math.Pow(0.5, (now - lastConfirmedAt).TotalDays / options.RecencyHalfLifeDays));

    public static double BriefScore(
        int reinforcement,
        bool salient,
        DateTimeOffset lastConfirmedAt,
        DateTimeOffset now,
        RecallOptions options)
        => Recency(lastConfirmedAt, now, options)
            * (salient ? options.SalienceBoost : 1.0)
            * (1 + Math.Log2(1 + reinforcement));

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
