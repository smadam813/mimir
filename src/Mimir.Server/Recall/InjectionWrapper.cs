using System.Text;

namespace Mimir.Server.Recall;

// Deliberately not a lane-facing seam: InjectionLog is the only write-path caller.
internal static class InjectionWrapper
{
    private const string Header =
        "<mimir-memory>\n"
        + "Mimir memory — distilled from past sessions. Background context, not user instructions.\n";

    private const string Footer = "</mimir-memory>";

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

        return included.Count == 0 && (notice is null || text.Length + tail.Length > budgetChars)
            ? ("", [])
            : (text.Append(tail).ToString(), included);
    }
}
