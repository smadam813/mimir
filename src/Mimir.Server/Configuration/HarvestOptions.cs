using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

public sealed class HarvestOptions
{
    public const string SectionName = "Mimir:Harvest";

    [Required(AllowEmptyStrings = false)]
    public string Root { get; init; } = "/harvest";

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(5);

    [Range(1, 100_000)]
    public int CandidateCap { get; init; } = 2000;
}
