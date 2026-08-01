using System.Globalization;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

internal static class McpTexts
{
    public const string UnknownProject = "unknown project";

    public static string UnknownKind(string kind)
        => $"Unknown kind '{kind}' — expected one of: "
            + string.Join(", ", Enum.GetNames<WisdomKind>()) + ".";

    public static string SealState(DateTimeOffset? sealedAt, string? reason)
        => sealedAt is { } at ? $"sealed {Timestamp(at)} ({reason ?? "no reason"})" : "live";

    public static string Date(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Timestamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";
}
