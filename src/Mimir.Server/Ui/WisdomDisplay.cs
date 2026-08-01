using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

public enum VersionChainView
{
    Changed,
    Full,
}

public enum TextChange
{
    Kept,
    Removed,
    Added,
}

public sealed record TextRun(TextChange Change, string Text);

public sealed record VersionEntry(
    int Version,
    WisdomVersionCause Cause,
    DateTimeOffset? At,
    string Text,
    IReadOnlyList<TextRun> Changed,
    bool Pending);

public sealed record CauseGloss(string Word, string Meaning);

public static class WisdomDisplay
{
    public const int ReinforcementBarSegments = 8;

    public static int ReinforcementFilled(int reinforcement)
        => Math.Clamp(reinforcement, 0, ReinforcementBarSegments);

    public static string? UnsavableReason(string draft, string current)
        => MergeGate.NoOpOf(draft, current) switch
        {
            WisdomEditNoOp.Blank => "Empty — a Wisdom's words cannot be blank.",
            WisdomEditNoOp.Unchanged => "Unchanged — this is what it already says.",
            _ => null,
        };

    public static string EditExplanation(int nextVersion, int reinforcement)
        => $"Saving goes through the Merge Gate: it appends v{nextVersion} · cause=edited, "
            + "re-embeds the new text, and waits behind any in-flight Admission batch. "
            + $"Reinforcement stays ×{reinforcement:N0} and recency does not move — an edit "
            + "rewords, it does not confirm (§6).";

    public static string CharacterCount(int length)
        => length == 1 ? "1 character" : $"{length:N0} characters";

    public static string VersionCount(int versions)
        => versions == 1 ? "1 version" : $"{versions:N0} versions";

    // Display only: the cause is persisted as a string, so renaming a member is a data migration.
    public static string CauseWord(WisdomVersionCause cause) => cause switch
    {
        WisdomVersionCause.Merged => "reinforced",
        _ => cause.ToString().ToLowerInvariant(),
    };

    public static IReadOnlyList<CauseGloss> CauseLegend { get; } =
        [.. Enum.GetValues<WisdomVersionCause>()
            .Select(cause => new CauseGloss(CauseWord(cause), CauseMeaning(cause)))];

    private static string CauseMeaning(WisdomVersionCause cause) => cause switch
    {
        WisdomVersionCause.Distilled => "a model wrote it from a Sealed Episode.",
        WisdomVersionCause.Merged =>
            "something independent said the same thing and the gate rewrote both into one.",
        WisdomVersionCause.Adjudicated =>
            "the gate ruled on a contradiction and this wording survived it (§6.4).",
        WisdomVersionCause.Edited => "a curator reworded it; Reinforcement did not move.",
        // Unworded rather than worded wrong; unreachable until §3 gains a fifth cause.
        _ => "a cause this screen has no words for yet.",
    };

    public const int DiffWordBound = 1_000;

    public static IReadOnlyList<TextRun> Diff(string? previous, string current)
    {
        if (previous is null || previous == current)
        {
            return [new TextRun(TextChange.Kept, current)];
        }

        var before = Words(previous);
        var after = Words(current);
        if (before.Count > DiffWordBound || after.Count > DiffWordBound)
        {
            var whole = new List<TextRun>
            {
                new(TextChange.Removed, previous),
                new(TextChange.Added, current),
            };
            SeparateRemovedRuns(whole);
            return whole;
        }

        var beforeWords = before.Select(word => word.TrimEnd()).ToArray();
        var afterWords = after.Select(word => word.TrimEnd()).ToArray();

        var lcs = new int[before.Count + 1, after.Count + 1];
        for (var i = before.Count - 1; i >= 0; i--)
        {
            for (var j = after.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = beforeWords[i] == afterWords[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var runs = new List<TextRun>();
        var (gone, arrived) = (0, 0);
        while (gone < before.Count || arrived < after.Count)
        {
            if (gone < before.Count && arrived < after.Count
                && beforeWords[gone] == afterWords[arrived])
            {
                Append(runs, TextChange.Kept, after[arrived]);
                gone++;
                arrived++;
            }
            else if (gone < before.Count
                && (arrived == after.Count || lcs[gone + 1, arrived] >= lcs[gone, arrived + 1]))
            {
                Append(runs, TextChange.Removed, before[gone]);
                gone++;
            }
            else
            {
                Append(runs, TextChange.Added, after[arrived]);
                arrived++;
            }
        }

        SeparateRemovedRuns(runs);
        return runs;
    }

    private static void SeparateRemovedRuns(List<TextRun> runs)
    {
        for (var run = 0; run < runs.Count; run++)
        {
            if (runs[run].Change is not TextChange.Removed)
            {
                continue;
            }

            var text = runs[run].Text;
            if (run > 0 && OpenAtEnd(runs[run - 1].Text) && OpenAtStart(text))
            {
                text = " " + text;
            }

            if (run + 1 < runs.Count && OpenAtEnd(text) && OpenAtStart(runs[run + 1].Text))
            {
                text += " ";
            }

            runs[run] = runs[run] with { Text = text };
        }
    }

    private static bool OpenAtEnd(string text) => text is [.., var last] && !char.IsWhiteSpace(last);

    private static bool OpenAtStart(string text) => text is [var first, ..] && !char.IsWhiteSpace(first);

    public static IReadOnlyList<VersionEntry> Chain(IReadOnlyList<WisdomVersion> versions)
    {
        var chain = versions.OrderByDescending(version => version.Version).ToList();
        return [.. chain.Select((version, row) => new VersionEntry(
            version.Version,
            version.Cause,
            version.CreatedAt,
            version.Text,
            Diff(row + 1 < chain.Count ? chain[row + 1].Text : null, version.Text),
            Pending: false))];
    }

    public static IReadOnlyList<VersionEntry> WithPendingEdit(
        IReadOnlyList<VersionEntry> chain, string current, string? draft)
    {
        if (chain is not [{ } head, ..] || draft is null || MergeGate.NoOpOf(draft, current) is not null)
        {
            return chain;
        }

        return [
            new VersionEntry(
                head.Version + 1,
                WisdomVersionCause.Edited,
                At: null,
                draft.Trim(),
                Diff(current, draft.Trim()),
                Pending: true),
            .. chain,
        ];
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var at = 0;
        while (at < text.Length)
        {
            var start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            words.Add(text[start..at]);
        }

        return words;
    }

    private static void Append(List<TextRun> runs, TextChange change, string text)
    {
        if (runs is [.., { } last] && last.Change == change)
        {
            runs[^1] = last with { Text = last.Text + text };
            return;
        }

        runs.Add(new TextRun(change, text));
    }

    public static string ReinforcementUnit(int reinforcement)
        => reinforcement == 1 ? "confirmation" : "confirmations";

    public static string RecallNote(WisdomRecall recall)
        => recall.Injections == 0
            ? "No injection has carried this line yet, so nothing here has judged it."
            : $"{recall.Injections:N0} {(recall.Injections == 1 ? "injection has" : "injections have")} "
                + "carried this line, across every Project that recalled it, whole history; "
                + $"{recall.Unmarked:N0} still unmarked. A mark is left on an injection as a whole "
                + "(§9), so the two figures above count entries this line rode in rather than "
                + "verdicts on the line itself.";

    public static string RetireHint(bool retired)
        => retired
            ? "Unretire puts it back into recall and default search."
            : "Retire is reversible — it changes standing, not words.";

    public static string? ProvenanceNote(IReadOnlyList<ProvenanceEntry> links)
        => links.Any(l => l.EpisodeId is not null)
            ? "Each session link opens the Episode it was captured in, anchored on the moment itself."
            : null;

    public static string ProvenanceTitle(ProvenanceEntry link) => link switch
    {
        { EventAt: { } at } => EpisodeDisplay.Stamp(at),
        { EpisodeStartedAt: { } at } => EpisodeDisplay.Stamp(at),
        { HarvestedItemId: not null } => "Harvested from auto-memory",
        // Unreachable by construction; the arm exists so a renderer cannot die on it.
        _ => "An unrecorded source",
    };

    public static string ProvenanceDetail(ProvenanceEntry link)
    {
        var parts = new List<string>(3);
        var where = link.EpisodeCwd is { } cwd ? $" in {cwd}" : "";
        if (link.EventType is { } type)
        {
            parts.Add(EpisodeDisplay.EventWord(type) + where);
            if (link.EventSeq is { } seq)
            {
                parts.Add($"Event #{seq}");
            }
        }
        else if (link.EpisodeId is not null)
        {
            parts.Add("the session" + where);
        }
        else if (link.HarvestedItemId is null)
        {
            // The unreachable shape again — see ProvenanceTitle.
            parts.Add("nothing this row still points at");
        }

        if (link.HarvestedPath is { } path)
        {
            parts.Add(path);
        }

        return string.Join(" · ", parts);
    }
}
