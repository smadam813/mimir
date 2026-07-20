using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: harvest scan interval, plus where the read-only auto-memory mount lives (§12). The
/// first scan of a fresh database is the Backfill — same code path, no knob for it.
/// </summary>
public sealed class HarvestOptions
{
    public const string SectionName = "Mimir:Harvest";

    /// <summary>
    /// The read-only bind mount of the host's <c>~/.claude/projects</c> (§12). Running on the
    /// host instead of in Compose, point this at that directory itself.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Root { get; init; } = "/harvest";

    /// <summary>Steady-state rescan interval; SessionEnd scans opportunistically in between.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(5);
}
