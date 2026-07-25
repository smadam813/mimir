using System.Globalization;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>
/// The one producer of the §7 label line a surface puts around a Wisdom it hands to a session —
/// <c>- [{kind} · {scope} · confirmed {date}{extra}] {text}</c>. Both ambient lanes render through
/// <see cref="InjectionLog"/>, and <c>mimir_search</c>'s Wisdom leg renders here directly; the two
/// surfaces differ deliberately in everything around the line (wrapper, budget, scope wording, the
/// Retired tag), so what is shared is the line itself and nothing else.
///
/// The scope text and the trailing extra are the caller's — the ambient lanes say
/// "Global"/"this project" and add nothing, <c>mimir_search</c> names the Project and tags a
/// Retired row. The shape and the date rule are not: dates render in UTC, never the value's own
/// offset. Two surfaces formatting the same <see cref="DateTimeOffset"/> differently is the
/// divergence this builder exists to make unrepresentable — only Npgsql's offset-zero timestamptz
/// reads kept the two of them agreeing before.
///
/// Deliberately its own date rule rather than <see cref="McpTexts.Date"/>'s: those are MCP wording
/// for Episode sections and timeline entries, and a change there must not silently rewrite what a
/// Brief puts in front of a session.
/// </summary>
internal static class InjectionLabel
{
    /// <param name="scope">Where the Wisdom holds, in the caller's own words.</param>
    /// <param name="extra">A trailing tag inside the bracket (" · Retired …"), or "" for none.</param>
    /// <returns>The label line, newline included — every surface renders it as its own line.</returns>
    public static string Line(
        WisdomKind kind,
        string scope,
        DateTimeOffset confirmedAt,
        string text,
        string extra = "")
        => $"- [{kind} · {scope} · confirmed {Date(confirmedAt)}{extra}] {text}\n";

    /// <summary>A label's date: the UTC calendar day, whatever offset the value carries.</summary>
    public static string Date(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
