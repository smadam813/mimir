using System.ComponentModel.DataAnnotations;
using Mimir.Server.Distillation;

namespace Mimir.Server.Configuration;

public sealed class DistillationOptions
{
    public const string SectionName = "Mimir:Distillation";

    [Range(0.0, 1.0)]
    public double MergeMatchThreshold { get; init; } = 0.80;

    public TimeSpan ContestedDuration { get; init; } = TimeSpan.FromDays(14);

    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);

    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan StaleRunningAfter { get; init; } = TimeSpan.FromHours(1);

    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan CrashSealIdleAfter { get; init; } = TimeSpan.FromHours(24);

    [Range(1024, DistillerCall.ContextTokens)]
    public int ChunkTokens { get; init; } = 12_288;
}
