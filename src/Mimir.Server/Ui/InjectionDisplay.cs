using System.Globalization;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// The §7 score a lane ranked by, in words: the expression itself, and the factors that feed it
/// read off the live <see cref="RecallOptions"/> rather than restated — a knob retuned in §11 must
/// not leave the screen explaining the old one.
/// </summary>
internal sealed record ScoringFormula(string Expression, string Factors);

/// <summary>
/// The §8.3 surface's shared presentation, sibling to <see cref="EpisodeDisplay"/>: how a lane is
/// named, what it costs, the §7 formula it ranked by, and the payload rebuild.
///
/// Pure by construction, and deliberately so — the payload rebuild is the part of this surface that
/// can be wrong with no database in the picture, and a pin that needs Postgres to run is a pin that
/// skips on the machine where the mistake is being made (the prior art is
/// <see cref="InjectionLabel"/> and <see cref="InjectionWrapper"/> themselves).
/// </summary>
internal static class InjectionDisplay
{
    /// <summary>
    /// A log row's stamp: the time alone, because the row's session header already carries the day
    /// and <see cref="EpisodeDisplay.Stamp"/>'s full form would repeat it on every line.
    /// </summary>
    public static string TimeOfDay(DateTimeOffset at)
        => at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The lane's own name, as §3 writes it — <c>Mcp</c> is an initialism, not a word.</summary>
    public static string Name(InjectionLane lane) => lane switch
    {
        InjectionLane.Brief => "Brief",
        InjectionLane.Prompt => "Prompt",
        _ => "MCP",
    };

    /// <summary>When the lane fired, in the words §3 describes it by.</summary>
    public static string Trigger(InjectionLane lane) => lane switch
    {
        InjectionLane.Brief => "at SessionStart",
        InjectionLane.Prompt => "on a prompt",
        _ => "the session asked",
    };

    /// <summary>
    /// The §11 char budget the lane filled to, or null for <c>Mcp</c> — <c>mimir_search</c> is
    /// capped by result count, not chars, so quoting a char budget for it would invent one.
    /// </summary>
    public static int? Budget(InjectionLane lane, RecallOptions options) => lane switch
    {
        InjectionLane.Brief => options.BriefBudgetChars,
        InjectionLane.Prompt => options.PromptBudgetChars,
        _ => null,
    };

    /// <summary>
    /// One score, at a precision that can tell two of them apart. §3's score-scale rule keeps the
    /// lanes' scales incomparable, and they are orders of magnitude apart: a brief_score runs to
    /// single digits while a fused query score sits near a hundredth, where two decimals would
    /// round every row in an entry to the same figure.
    /// </summary>
    public static string Score(double score)
        => score.ToString(score >= 1 ? "0.00" : "0.0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// The §7 score this lane ranked by. The Brief has no query at session start, so it ranks on
    /// each Wisdom's own record; the two query lanes fuse a ranked search instead and damp
    /// reinforcement far more gently. Two expressions, because there really are two.
    ///
    /// The expressions restate <see cref="RecallScoring.BriefScore"/> and
    /// <see cref="RecallScoring.QueryScore"/>, and deliberately: one is arithmetic a lane runs, the
    /// other is prose a curator reads, and rendering a curator-facing formula out of the live
    /// computation is not a thing C# can do. What is *not* restated is the numbers in them — every
    /// factor below is read off <see cref="RecallOptions"/>, so a §11 retune cannot leave this
    /// explaining the old one. Change either scoring method's shape and change these too.
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

    /// <summary>
    /// The §7 wrapper an ambient lane put in front of the session, rebuilt from what the entry
    /// recorded: the same <see cref="InjectionWrapper"/> and <see cref="InjectionLabel"/> the lane
    /// itself rendered through, over the same Wisdom in the same order.
    ///
    /// It is a rebuild rather than a replay of stored text, because §3 records an injection's size
    /// and items and not its payload. Two things therefore make the rebuild differ from what the
    /// session read, and both are visible on the screen rather than papered over: a line whose
    /// Wisdom was edited or hard-deleted since, and the Brief's growth-tripwire notice, which rides
    /// in the payload's char count but is not itself recorded. The recorded <c>chars</c> is the
    /// check — an entry whose rebuild is a different length has drifted.
    /// </summary>
    /// <returns>
    /// The wrapper text; <c>""</c> when no carried Wisdom survives to rebuild a line from; and null
    /// for <c>Mcp</c>, whose payload was never this wrapper at all — <c>mimir_search</c> composes
    /// its own sectioned answer, Episodes included, and only its Wisdom lines are recorded.
    /// </returns>
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

        // Unbounded: the recorded items are exactly the ones the lane's own §11 budget admitted, so
        // measuring them against that budget a second time would be a different fill, not a rebuild
        // — one whose header and footer are charged twice against a line that only just fitted.
        return InjectionWrapper.Build(entries, int.MaxValue).Text;
    }

    /// <summary>
    /// Why an entry cannot be promoted to a GoldenCase (§9), or null when it can. Three faults, and
    /// naming the wrong one is worse than saying nothing: a Brief carries no query to replay (§3);
    /// an <c>mimir_search</c> whose answer matched only Episodes carried no Wisdom at all, an
    /// ordinary outcome and nothing to do with retirement; and only the third is the entry whose
    /// lines have since been retired or hard-deleted. <see cref="InjectionLogEntry.CanPromote"/>
    /// collapses all three into one false, so the reason is worked out here rather than inferred
    /// from the query alone.
    /// </summary>
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
