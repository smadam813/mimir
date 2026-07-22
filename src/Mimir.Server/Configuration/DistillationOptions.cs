using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>Spec §11: the Merge Gate and Distiller knobs.</summary>
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

    /// <summary>§6.4: how long adjudication's Contested flag lives before the sweep clears it.</summary>
    public TimeSpan ContestedDuration { get; init; } = TimeSpan.FromDays(14);

    /// <summary>§6: the cadence of the sweep that re-queues, resets, and crash-Seals.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>§6: a Running claim older than this is a dead worker's; the sweep resets it.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan StaleRunningAfter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>§4: an unsealed Episode idle this long is crashed; the sweep Seals it.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan CrashSealIdleAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// §6: an Episode whose Event stream exceeds this is chunked chronologically and distilled
    /// per chunk (~12K tokens, leaving prompt-and-answer room inside the 16384 context).
    /// </summary>
    [Range(1024, 1_000_000)]
    public int ChunkTokens { get; init; } = 12_288;
}
