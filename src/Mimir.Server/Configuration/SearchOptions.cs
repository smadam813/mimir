using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

public sealed class SearchOptions
{
    public const string SectionName = "Mimir:Search";

    [Range(1, 1000)]
    public int RrfK { get; init; } = 60;

    [Range(1, 1000)]
    public int PerLegTopN { get; init; } = 50;

    [Range(1, 1000)]
    public int GoldenSetK { get; init; } = 5;
}
