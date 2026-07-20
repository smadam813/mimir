using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: event payload cap — 4 KB per payload field, kept as 3 KB head + 1 KB tail. The loss
/// is deliberate (ADR-0003): capture is lossy-by-design and the original size is always recorded.
/// </summary>
public sealed class CaptureOptions
{
    public const string SectionName = "Mimir:Capture";

    /// <summary>A string field at most this many UTF-8 bytes is stored whole.</summary>
    [Range(1, int.MaxValue)]
    public int PayloadFieldCapBytes { get; init; } = 4096;

    /// <summary>How much of an oversized field's start survives.</summary>
    [Range(1, int.MaxValue)]
    public int PayloadHeadBytes { get; init; } = 3072;

    /// <summary>How much of an oversized field's end survives.</summary>
    [Range(1, int.MaxValue)]
    public int PayloadTailBytes { get; init; } = 1024;
}
