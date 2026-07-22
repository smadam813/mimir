using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: the Recall-lane knobs the Brief uses (§7) — the budget it fills to and the recency
/// and salience factors of its score. The Prompt lane's own knobs (its budget, the cosine gate)
/// arrive with its ticket.
/// </summary>
public sealed class RecallOptions
{
    public const string SectionName = "Mimir:Recall";

    /// <summary>§7: the Brief is filled to at most this many chars, wrapper included.</summary>
    [Range(1, 100_000)]
    public int BriefBudgetChars { get; init; } = 4000;

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
