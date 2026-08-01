using System.Globalization;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

internal static class InjectionLabel
{
    public static string Line(
        WisdomKind kind,
        string scope,
        DateTimeOffset confirmedAt,
        string text,
        string extra = "")
        => $"- [{kind} · {scope} · confirmed {Date(confirmedAt)}{extra}] {text}\n";

    public static string Date(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
