using System.Globalization;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

internal sealed record ScoringFormula(string Expression, string Factors);

internal static class InjectionDisplay
{
    public static string TimeOfDay(DateTimeOffset at)
        => at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Name(InjectionLane lane) => lane switch
    {
        InjectionLane.Brief => "Brief",
        InjectionLane.Prompt => "Prompt",
        _ => "MCP",
    };

    public static string Trigger(InjectionLane lane) => lane switch
    {
        InjectionLane.Brief => "at SessionStart",
        InjectionLane.Prompt => "on a prompt",
        _ => "the session asked",
    };

    public static int? Budget(InjectionLane lane, RecallOptions options) => lane switch
    {
        InjectionLane.Brief => options.BriefBudgetChars,
        InjectionLane.Prompt => options.PromptBudgetChars,
        _ => null,
    };

    public static string Score(double score)
        => score.ToString(score >= 1 ? "0.00" : "0.0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// The §7 score this lane ranked by. The Brief has no query at session start, so it ranks on
    /// each Wisdom's own record; the two query lanes fuse a ranked search instead and damp
    /// reinforcement far more gently. Two expressions, because there really are two.
    /// </summary>
    public static ScoringFormula Formula(InjectionLane lane, RecallOptions options)
    {
        var recency = $"Recency halves every {Number(options.RecencyHalfLifeDays)} days since last "
            + $"confirmation and never falls below {Number(options.RecencyFloor)}; a Wisdom whose "
            + $"Provenance carries a deliberate save takes a ×{Number(options.SalienceBoost)} "
            + "salience boost.";

        return lane == InjectionLane.Brief
            ? new ScoringFormula(
                "brief_score = recency × salience × (1 + log₂(1 + Reinforcement))",
                recency + " No query exists at session start, so rank comes from each line's own "
                    + "record. The universe is this Project plus Global, minus Retired.")
            : new ScoringFormula(
                "score = RRF(vector, FTS) × affinity × recency × salience × (1 + ln(1 + Reinforcement) ÷ 10)",
                recency + $" Wisdom scoped to the session's own Project takes a ×{Number(options.AffinityBoost)} "
                    + "affinity boost, never Global. With a query in hand relevance leads, so "
                    + "reinforcement only nudges.");
    }

    public static string? Payload(InjectionLane lane, IReadOnlyList<InjectedWisdom> items)
    {
        if (lane == InjectionLane.Mcp)
        {
            return null;
        }

        var entries = items
            .Where(i => i.Wisdom is not null)
            .Select(i => new InjectionEntry(
                i.WisdomId,
                i.Score,
                i.Wisdom!.Kind,
                i.Wisdom.ScopeProjectId == Project.GlobalId,
                i.Wisdom.LastConfirmedAt,
                i.Wisdom.Text))
            .ToList();

        return InjectionWrapper.Build(entries, int.MaxValue).Text;
    }

    public static string? CannotPromote(InjectionLogEntry entry) => entry switch
    {
        { CanPromote: true } => null,
        { QueryContext: null } => "needs a query to replay; a Brief carries none.",
        { Items.Count: 0 } => "this entry carried no Wisdom at all, so no case could expect one.",
        _ => "every Wisdom it carried is retired or deleted, so no case could expect one.",
    };

    private static string Number(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
