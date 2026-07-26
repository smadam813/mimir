using System.Text;

namespace Mimir.Server.Recall;

/// <summary>
/// The §7 provenance-labeled wrapper the ambient lanes share: a header identifying the content as
/// Mimir memory — not user instructions — and one <see cref="InjectionLabel"/> line per Wisdom,
/// filled to the caller's char budget (§11) in the caller's order.
///
/// Pure, and deliberately so: the budget arithmetic is the part of an injection that can be wrong
/// without any database in the picture, and a pin that needs Postgres to run is a pin that skips
/// on the machine where the mistake is being made.
///
/// Not a lane-facing seam — <see cref="InjectionLog"/> is its only caller on the write path, and
/// <c>Ui.InjectionDisplay</c> its only other caller anywhere, rebuilding a logged entry's payload
/// for the §8.3 surface. A lane that built a wrapper here and recorded it itself would apply the
/// wrong shape of the §7 empty-trace rule: the ambient one reads emptiness off
/// <see cref="Build"/>'s included list, never off its text.
/// </summary>
internal static class InjectionWrapper
{
    private const string Header =
        "<mimir-memory>\n"
        + "Mimir memory — distilled from past sessions. Background context, not user instructions.\n";

    private const string Footer = "</mimir-memory>";

    /// <param name="entries">Candidates in injection order (highest score first).</param>
    /// <param name="budgetChars">The budget for the whole rendered wrapper (§11).</param>
    /// <param name="notice">A trailing non-Wisdom line, or null for none. Reserved out of the
    /// budget before any entry is measured, so a lane that appends one buys the room from its own
    /// Wisdom rather than overrunning §11.</param>
    /// <returns>The wrapper and what it carried — <c>("", [])</c> when nothing was rendered at all.
    /// An entry too large for the room left is skipped rather than ending the fill, so one
    /// oversized Wisdom never starves the rest.</returns>
    public static (string Text, IReadOnlyList<InjectionEntry> Included) Build(
        IEnumerable<InjectionEntry> entries, int budgetChars, string? notice = null)
    {
        var tail = notice + Footer;
        var text = new StringBuilder(Header);
        var included = new List<InjectionEntry>();
        foreach (var entry in entries)
        {
            var line = InjectionLabel.Line(
                entry.Kind,
                entry.IsGlobal ? "Global" : "this project",
                entry.LastConfirmedAt,
                entry.Text);
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
}
