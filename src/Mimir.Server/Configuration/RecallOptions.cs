using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: the Recall-lane knobs — the budgets the two ambient lanes fill to, the Prompt lane's
/// cosine gate, and the factors of the §7 scores (brief_score and the query ranking).
/// </summary>
public sealed class RecallOptions
{
    public const string SectionName = "Mimir:Recall";

    /// <summary>§7: the Brief is filled to at most this many chars, wrapper included.</summary>
    [Range(1, 100_000)]
    public int BriefBudgetChars { get; init; } = 4000;

    /// <summary>§7: the Prompt lane fills to at most this many chars, wrapper included.</summary>
    [Range(1, 100_000)]
    public int PromptBudgetChars { get; init; } = 1500;

    /// <summary>
    /// §7: the Prompt lane injects only when the best eligible match reaches this cosine — per
    /// the §3 score-scale rule a cosine similarity, never compared against fused scores.
    /// </summary>
    [Range(-1.0, 1.0)]
    public double PromptGateCosine { get; init; } = 0.75;

    /// <summary>§7: the query-ranking factor for Wisdom scoped to the session's own Project.</summary>
    [Range(1.0, 10.0)]
    public double AffinityBoost { get; init; } = 1.5;

    /// <summary>§7: recency halves every this many days since last confirmation.</summary>
    [Range(1.0, 10_000.0)]
    public double RecencyHalfLifeDays { get; init; } = 90;

    /// <summary>§7: recency never decays below this floor.</summary>
    [Range(0.0, 1.0)]
    public double RecencyFloor { get; init; } = 0.3;

    /// <summary>§7: the factor for Wisdom with a salient Provenance Event (a deliberate save).</summary>
    [Range(1.0, 10.0)]
    public double SalienceBoost { get; init; } = 1.3;
}
