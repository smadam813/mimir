using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: the Merge Gate knob this ticket needs. The Distiller's own knobs (sweep cadence,
/// chunking, contested duration) arrive with the Distiller ticket.
/// </summary>
public sealed class DistillationOptions
{
    public const string SectionName = "Mimir:Distillation";

    /// <summary>
    /// The §6 match threshold: a candidate whose best vector-leg cosine reaches this merges into
    /// the matched Wisdom; below it, the candidate becomes new Wisdom. A cosine similarity — the
    /// §3 score-scale rule keeps RRF-fused values out of every threshold comparison.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MergeMatchThreshold { get; init; } = 0.80;
}
