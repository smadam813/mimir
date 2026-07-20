namespace Mimir.Server.Ui;

/// <summary>
/// The §8.2 surfaces' shared presentation: timeline rows and the drill-down must describe the
/// same Episode the same way, so the words live in one place.
/// </summary>
public static class EpisodeDisplay
{
    public static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");

    /// <summary>Unsealed means live (or crashed, §4); a Seal always shows its reason.</summary>
    public static string StateLabel(DateTimeOffset? sealedAt, string? sealReason)
        => sealedAt is null ? "live" : $"sealed · {sealReason ?? "no reason"}";
}
