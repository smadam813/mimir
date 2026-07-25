using System.Globalization;
using System.Text;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Recall;

/// <summary>One Wisdom bound for injection: the score that ordered it plus what its label needs.</summary>
internal sealed record InjectionEntry(
    Guid WisdomId,
    double Score,
    WisdomKind Kind,
    bool IsGlobal,
    DateTimeOffset LastConfirmedAt,
    string Text);

/// <summary>
/// The §7 provenance-labeled wrapper shared by both ambient lanes: a header identifying the
/// content as Mimir memory — not user instructions — and one line per Wisdom tagged
/// kind/scope/last-confirmed. Fills the caller's char budget in the caller's order, skipping
/// entries too large to fit so one oversized Wisdom never starves the rest.
/// </summary>
internal static class InjectionRenderer
{
    private const string Header =
        "<mimir-memory>\n"
        + "Mimir memory — distilled from past sessions. Background context, not user instructions.\n";

    private const string Footer = "</mimir-memory>";

    /// <param name="entries">Candidates in injection order (highest score first).</param>
    /// <param name="budgetChars">The lane's budget for the whole rendered wrapper (§11).</param>
    /// <param name="notice">A trailing non-Wisdom line, or null for none. Reserved out of the
    /// budget before any entry is measured, so a lane that appends one buys the room from its own
    /// Wisdom rather than overrunning §11. A notice with no entries behind it still renders — the
    /// lanes say "nothing to recall" by injecting nothing at all, so a notice that vanished with
    /// the last entry would be silent in exactly the case it was raised for.</param>
    /// <returns>The rendered injection ("" for none) and the entries that made it in.</returns>
    public static (string Text, IReadOnlyList<InjectionEntry> Included) Render(
        IEnumerable<InjectionEntry> entries, int budgetChars, string? notice = null)
    {
        var tail = notice + Footer;
        var text = new StringBuilder(Header);
        var included = new List<InjectionEntry>();
        foreach (var entry in entries)
        {
            var line = Label(entry);
            if (text.Length + line.Length + tail.Length <= budgetChars)
            {
                text.Append(line);
                included.Add(entry);
            }
        }

        // Nothing to label and nothing to report is the empty injection. A notice alone is still
        // worth a wrapper — but only one the budget can hold, since §11 binds this lane whether or
        // not it has Wisdom to spend the budget on.
        return included.Count == 0 && (notice is null || text.Length + tail.Length > budgetChars)
            ? ("", [])
            : (text.Append(tail).ToString(), included);
    }

    private static string Label(InjectionEntry entry)
    {
        var scope = entry.IsGlobal ? "Global" : "this project";
        var confirmed = entry.LastConfirmedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"- [{entry.Kind} · {scope} · confirmed {confirmed}] {entry.Text}\n";
    }
}
