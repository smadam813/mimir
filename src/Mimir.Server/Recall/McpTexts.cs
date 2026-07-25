using System.Globalization;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>Wording and formats the MCP tools share, kept together so they cannot drift.</summary>
internal static class McpTexts
{
    public const string UnknownProject = "unknown project";

    public static string UnknownKind(string kind)
        => $"Unknown kind '{kind}' — expected one of: "
            + string.Join(", ", Enum.GetNames<WisdomKind>()) + ".";

    /// <summary>An Episode's seal state (§4): live, or sealed with its reason.</summary>
    public static string SealState(DateTimeOffset? sealedAt, string? reason)
        => sealedAt is { } at ? $"sealed {Timestamp(at)} ({reason ?? "no reason"})" : "live";

    /// <summary>
    /// A date in an MCP answer's own wording — Episode sections and timeline entries. Injection
    /// label lines do not read this: <see cref="InjectionLabel"/> keeps their date rule, so a
    /// change here cannot rewrite what a Brief puts in front of a session.
    /// </summary>
    public static string Date(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Timestamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";
}
