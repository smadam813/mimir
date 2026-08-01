using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

public sealed class RecallOptions
{
    public const string SectionName = "Mimir:Recall";

    [Range(1, 100_000)]
    public int BriefBudgetChars { get; init; } = 4000;

    [Range(1, 100_000)]
    public int PromptBudgetChars { get; init; } = 1500;

    [Range(-1.0, 1.0)]
    public double PromptGateCosine { get; init; } = 0.75;

    [Range(1.0, 10.0)]
    public double AffinityBoost { get; init; } = 1.5;

    [Range(1.0, 10_000.0)]
    public double RecencyHalfLifeDays { get; init; } = 90;
    [Range(0.0, 1.0)]
    public double RecencyFloor { get; init; } = 0.3;

    [Range(1.0, 10.0)]
    public double SalienceBoost { get; init; } = 1.3;
}
