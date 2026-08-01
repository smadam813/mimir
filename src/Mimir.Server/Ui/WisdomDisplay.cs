using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// Which of the two ways the §8.1 version chain reads (#93). <see cref="Changed"/> is what the
/// screen opens on, because a chain of near-identical paragraphs is exactly what the marks exist
/// to stop a curator diffing by eye; <see cref="Full"/> is the way out when the diff is not what
/// they came for.
/// </summary>
public enum VersionChainView
{
    Changed,
    Full,
}

/// <summary>
/// What one run of words did between two versions. A <see cref="Removed"/> run carries the
/// <em>previous</em> version's words — they are not in this version's text at all — which is why a
/// row's own text is exactly its <see cref="Kept"/> and <see cref="Added"/> runs, concatenated.
/// </summary>
public enum TextChange
{
    Kept,
    Removed,
    Added,
}

/// <summary>One such run: the words, and what became of them.</summary>
public sealed record TextRun(TextChange Change, string Text);

/// <summary>
/// One row of the rendered version chain. <see cref="At"/> is null and <see cref="Pending"/> true
/// for the row no gate has seen: the draft in the editor, drawn at the head so a curator can read
/// their own rewording against what stands without saving it first.
/// </summary>
public sealed record VersionEntry(
    int Version,
    WisdomVersionCause Cause,
    DateTimeOffset? At,
    string Text,
    IReadOnlyList<TextRun> Changed,
    bool Pending);

/// <summary>One line of the chain's legend: the badge word, and what it means.</summary>
public sealed record CauseGloss(string Word, string Meaning);

/// <summary>
/// The §8.1 detail's shared presentation, sibling to <see cref="EpisodeDisplay"/>: how one
/// Provenance row reads, how far the Reinforcement bar fills, what a version chain changed from
/// one row to the next, and every sentence the detail says about a figure rather than renders as
/// one.
///
/// Pure by construction, and deliberately so — a pin that needs Postgres to run is a pin that skips
/// on the machine where the mistake is being made (the prior art is <see cref="InjectionDisplay"/>
/// and the recall module's own builders). bUnit renders components here now, but only as the
/// ladder's last rung (#130): wording is a rule about what is *computed*, so it stays on this side
/// of the seam and a render test's job is at most that the markup asks for it.
/// </summary>
public static class WisdomDisplay
{
    /// <summary>
    /// How many segments the aside's Reinforcement bar is drawn from. A fixed row, so a line
    /// confirmed once and a line confirmed eleven times look different at a glance without the
    /// figure beside it having to be read — and so the bar cannot outgrow the 283px it sits in.
    /// </summary>
    public const int ReinforcementBarSegments = 8;

    /// <summary>
    /// How many of those segments a Reinforcement count lights. Clamped at both ends: the figure
    /// itself is beside the bar and says what the clamp swallowed.
    /// </summary>
    public static int ReinforcementFilled(int reinforcement)
        => Math.Clamp(reinforcement, 0, ReinforcementBarSegments);

    /// <summary>
    /// Why saving the editor's draft would change nothing, or null when it would land: two of
    /// <see cref="MergeGate"/>'s three no-ops worded for the curator in front of the
    /// button rather than discovered by pressing it. Which two is
    /// <see cref="MergeGate.NoOpOf"/>'s to decide, not this method's — a second
    /// statement of that set is the drift #71 was, and here it would gate a button rather than sit
    /// in a comment. The third (an id nothing answers to) has no wording because the detail could
    /// not have rendered; unworded, Save stays enabled and the gate decides, as it does anyway.
    /// </summary>
    public static string? UnsavableReason(string draft, string current)
        => MergeGate.NoOpOf(draft, current) switch
        {
            WisdomEditNoOp.Blank => "Empty — a Wisdom's words cannot be blank.",
            WisdomEditNoOp.Unchanged => "Unchanged — this is what it already says.",
            _ => null,
        };

    /// <summary>
    /// What Save will do, in the curator's words beside the button that does it (§8.1's own
    /// criterion). It states <see cref="Distillation.MergeGate.EditAsync"/>'s mechanics, so it
    /// lives here where a test can hold it to them rather than in an <c>@code</c> block a renderer
    /// could only quote: change the gate's version numbering, its cause, or what it leaves Reinforcement and
    /// recency doing, and this sentence is the other place to change.
    /// </summary>
    public static string EditExplanation(int nextVersion, int reinforcement)
        => $"Saving goes through the Merge Gate: it appends v{nextVersion} · cause=edited, "
            + "re-embeds the new text, and waits behind any in-flight Admission batch. "
            + $"Reinforcement stays ×{reinforcement:N0} and recency does not move — an edit "
            + "rewords, it does not confirm (§6).";

    /// <summary>
    /// How long a text is, in the unit a lane fills its §11 char budget out of — so the figure is a
    /// count of what a session would receive rather than of anything stored on disk. Two callers
    /// with different tenses read it: over the editor it is the draft's live length and moves as the
    /// curator types, and in the chain's full-text view it is one saved version's, fixed.
    /// </summary>
    public static string CharacterCount(int length)
        => length == 1 ? "1 character" : $"{length:N0} characters";

    /// <summary>How long the chain is, beside the heading that names it.</summary>
    public static string VersionCount(int versions)
        => versions == 1 ? "1 version" : $"{versions:N0} versions";

    /// <summary>
    /// The badge word for a §3 cause. <see cref="WisdomVersionCause.Merged"/> is the one mapping:
    /// the enum names the text operation the gate performed, the badge names the Admission outcome
    /// the curator is looking at, and <em>reinforced</em> is the domain's word for that outcome.
    /// Display only — the cause is persisted as a string, so renaming the member would be a data
    /// migration and this port is not one (#86). Every other cause reads as its own name, so a
    /// fifth added to §3 says what it is rather than passing silently as one of these four.
    /// </summary>
    public static string CauseWord(WisdomVersionCause cause) => cause switch
    {
        WisdomVersionCause.Merged => "reinforced",
        _ => cause.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// What each badge word means, spelled out under the chain (§8.1's own criterion) rather than
    /// looked up. Built from the enum in its own order, zeros of the same kind the lane rows keep:
    /// a cause §3 gains cannot be silently missing from the legend that claims to define them. The
    /// drawn design named three — <em>adjudicated</em> is restored, because the Merge Gate writes
    /// it at three separate call sites (#93).
    /// </summary>
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
        // Unworded rather than worded wrong: a cause added to §3 without a sentence here says so,
        // which is a legend a curator can act on. CauseWord still names it.
        _ => "a cause this screen has no words for yet.",
    };

    /// <summary>
    /// How many words either side of a diff may carry before it stops being drawn word by word.
    /// The diff is quadratic in the two texts and the pane draws one per version, so the bound is
    /// what keeps a pathological row from taking the render down with it. A Wisdom this long could
    /// not fit even the Brief lane's whole §11 char budget, so past it the honest thing to say is
    /// that the text changed — not to spend a chain's worth of tables saying how.
    /// </summary>
    public const int DiffWordBound = 1_000;

    /// <summary>
    /// What one version's words changed against the version below it, as runs a row can draw in
    /// order: the kept text plain, the previous wording struck, the new wording marked. A null
    /// <paramref name="previous"/> is the foot of the chain — nothing to differ from, so it reads
    /// plain rather than wholly added, which would tell a curator a model added words to something
    /// when it wrote the line.
    ///
    /// Removed runs come before added ones at every point the two texts diverge, so a substitution
    /// reads as one — "<em>skips</em> never fires SaveChanges" — rather than as an arrival beside
    /// an unrelated deletion. The kept runs are drawn from <paramref name="current"/>, which is
    /// what makes the row exactly this version's own text — and what leaves a removed run at the
    /// edge of that text with no separator of its own, hence <see cref="SeparateRemovedRuns"/>.
    /// </summary>
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
            // Separated like any other pair: the wholesale answer is still two runs meeting in the
            // markup, and the last word of one fusing with the first of the other is the same
            // defect there as anywhere else.
            var whole = new List<TextRun>
            {
                new(TextChange.Removed, previous),
                new(TextChange.Added, current),
            };
            SeparateRemovedRuns(whole);
            return whole;
        }

        // Words are matched on themselves, without the whitespace they carry, so a rewrap does not
        // read as a rewording; the run that is drawn keeps the whitespace, so the text reassembles
        // exactly.
        var beforeWords = before.Select(word => word.TrimEnd()).ToArray();
        var afterWords = after.Select(word => word.TrimEnd()).ToArray();

        // lcs[i, j] is how many words the two texts still share from i and j onward, which is what
        // the walk below reads to decide whether the cheaper move is to drop a word or take one.
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

    /// <summary>
    /// Keeps a struck run clear of the words on either side of it. A word carries the whitespace
    /// that follows it, so a removal in the middle of a text separates itself — but one at either
    /// end of the row's own text has nothing between it and its neighbour, and
    /// <c>alpha <s>gamma</s>delta</c> is a rewording rendered as a single fused word. The
    /// separator goes onto the removed run rather than beside it, because everything else the walk
    /// emits is <em>this</em> version's text verbatim and must stay that way.
    /// </summary>
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

    /// <summary>
    /// The chain as the screen reads it: newest first, each row carrying what its words changed
    /// against the row below it.
    ///
    /// The order is this method's own rather than its caller's ORDER BY, because the pairing is
    /// what makes it load-bearing here: hand these rows back the other way up and every diff on
    /// the screen inverts silently, each version's arrivals drawn as departures.
    ///
    /// A function of the Wisdom alone, so it survives a burst of typing untouched — see
    /// <see cref="WithPendingEdit"/>, which is the half that does not.
    /// </summary>
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

    /// <summary>
    /// The same chain with the editor's draft above its head, as the version it would become — so
    /// a curator reads their own rewording against what stands without saving to see it. Split
    /// from <see cref="Chain"/> rather than folded into it because the two answer to different
    /// things: the saved rows change when the Wisdom does, this row on every keystroke, and one
    /// call doing both re-diffs the whole chain per character typed.
    ///
    /// Whether the draft is a version at all is <see cref="MergeGate.NoOpOf"/>'s to say — the one
    /// statement of that set, which <see cref="UnsavableReason"/> reads to gate the Save button
    /// beside it, so the chain never grows a pending row over a button that would do nothing.
    /// </summary>
    /// <param name="current">
    /// What the Wisdom says now, from the same row the Save button reads it from. Not taken off
    /// the head version, which only holds the same text because every rewrite goes through the
    /// gate — reading it here would be a second answer to a question the caller has already asked.
    /// </param>
    /// <param name="draft">
    /// What the editor holds, or null when it is closed. Trimmed into the row it heads, because
    /// trimmed is what the gate would write.
    /// </param>
    public static IReadOnlyList<VersionEntry> WithPendingEdit(
        IReadOnlyList<VersionEntry> chain, string current, string? draft)
    {
        // The row is numbered off the head, so a chain with no rows cannot carry one — a state the
        // schema does not allow while the Wisdom stands, and one WisdomDetail already guards for.
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

    /// <summary>
    /// One word and the whitespace that follows it, so the runs a row draws reassemble into the
    /// text exactly — an ordinary split would leave the diff inventing the spacing back.
    /// </summary>
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

    /// <summary>
    /// Adds a word to the run being drawn, or starts one. Merging here rather than in the renderer
    /// is what makes an unchanged sentence one plain run instead of a mark per word.
    /// </summary>
    private static void Append(List<TextRun> runs, TextChange change, string text)
    {
        if (runs is [.., { } last] && last.Change == change)
        {
            runs[^1] = last with { Text = last.Text + text };
            return;
        }

        runs.Add(new TextRun(change, text));
    }

    /// <summary>What the Reinforcement figure counts, in the singular a first Admission leaves.</summary>
    public static string ReinforcementUnit(int reinforcement)
        => reinforcement == 1 ? "confirmation" : "confirmations";

    /// <summary>
    /// What the Recall figures do and do not mean. The §9 mark is left on an injection as a whole
    /// (§3), so a "marked useful" against one line is a judgement of the entry it rode in — the
    /// aside says so rather than letting the row beside a Wisdom read as a verdict on the Wisdom.
    /// </summary>
    public static string RecallNote(WisdomRecall recall)
        => recall.Injections == 0
            ? "No injection has carried this line yet, so nothing here has judged it."
            : $"{recall.Injections:N0} {(recall.Injections == 1 ? "injection has" : "injections have")} "
                + "carried this line, across every Project that recalled it, whole history; "
                + $"{recall.Unmarked:N0} still unmarked. A mark is left on an injection as a whole "
                + "(§9), so the two figures above count entries this line rode in rather than "
                + "verdicts on the line itself.";

    /// <summary>
    /// What Retire is, beside the button that does it: reversible, and about standing rather than
    /// words (CONTEXT.md, Retire) — which is what keeps it distinguishable from the Delete below.
    /// </summary>
    public static string RetireHint(bool retired)
        => retired
            ? "Unretire puts it back into recall and default search."
            : "Retire is reversible — it changes standing, not words.";

    /// <summary>
    /// What the provenance list promises, or null when it can promise nothing: a row opens the
    /// Episode only where one is behind it, and a Wisdom harvested out of auto-memory has none.
    /// </summary>
    public static string? ProvenanceNote(IReadOnlyList<ProvenanceEntry> links)
        => links.Any(l => l.EpisodeId is not null)
            ? "Each session link opens the Episode it was captured in, anchored on the moment itself."
            : null;

    /// <summary>
    /// What a Provenance link is called: the moment the record was captured, or where it was
    /// harvested from. Never the session id — a curator recognizes "2026-07-24 08:12 UTC" and does
    /// not recognize <c>sess-6f2a…</c>, which the link itself carries anyway.
    /// </summary>
    public static string ProvenanceTitle(ProvenanceEntry link) => link switch
    {
        { EventAt: { } at } => EpisodeDisplay.Stamp(at),
        { EpisodeStartedAt: { } at } => EpisodeDisplay.Stamp(at),
        { HarvestedItemId: not null } => "Harvested from auto-memory",
        // Unreachable rather than a fourth shape: the gate writes no all-null link — a candidate
        // carrying nothing at all yields no row and the Wisdom is born Orphaned instead
        // (MergeGate.LinksOf) — and both nullable links cascade the row away rather than blanking
        // it. The arm exists because a renderer must never be the thing that dies.
        _ => "An unrecorded source",
    };

    /// <summary>
    /// The link's second line: what happened, where the session was running, and which Event it
    /// was. A row can carry a harvested path alongside an Episode link, so the path is appended
    /// rather than switched on.
    /// </summary>
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
            // A candidate that named no Events links to the session as a whole (§6).
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
