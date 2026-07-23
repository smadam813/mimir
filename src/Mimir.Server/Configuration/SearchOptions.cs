using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: the hybrid-search fusion knobs (§3). Fused RRF scores order candidates and nothing
/// else — every threshold in the system is a cosine similarity, never a fused score.
/// </summary>
public sealed class SearchOptions
{
    public const string SectionName = "Mimir:Search";

    /// <summary>Reciprocal Rank Fusion constant (§3).</summary>
    [Range(1, 1000)]
    public int RrfK { get; init; } = 60;

    /// <summary>Candidates each leg (vector KNN, FTS) contributes before fusion (§3).</summary>
    [Range(1, 1000)]
    public int PerLegTopN { get; init; } = 50;

    /// <summary>§9: a GoldenCase passes when its expected Wisdom ranks within this many.</summary>
    [Range(1, 1000)]
    public int GoldenSetK { get; init; } = 5;
}
