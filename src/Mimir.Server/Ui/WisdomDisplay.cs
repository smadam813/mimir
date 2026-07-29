using Mimir.Server.Distillation;

namespace Mimir.Server.Ui;

/// <summary>
/// The §8.1 detail's shared presentation, sibling to <see cref="EpisodeDisplay"/>: how one
/// Provenance row reads, how far the Reinforcement bar fills, and every sentence the detail says
/// about a figure rather than renders as one.
///
/// Pure by construction, and deliberately so — a pin that needs Postgres to run is a pin that skips
/// on the machine where the mistake is being made (the prior art is <see cref="InjectionDisplay"/>
/// and the recall module's own builders). It is also the only place a rule in this surface can be
/// pinned at all: nothing renders a component in this suite, so an <c>@code</c> block's wording is
/// wording nothing can hold to.
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
    /// lives here where a test can hold it to them rather than in markup nothing renders in this
    /// suite: change the gate's version numbering, its cause, or what it leaves Reinforcement and
    /// recency doing, and this sentence is the other place to change.
    /// </summary>
    public static string EditExplanation(int nextVersion, int reinforcement)
        => $"Saving goes through the Merge Gate: it appends v{nextVersion} · cause=edited, "
            + "re-embeds the new text, and waits behind any in-flight Admission batch. "
            + $"Reinforcement stays ×{reinforcement:N0} and recency does not move — an edit "
            + "rewords, it does not confirm (§6).";

    /// <summary>
    /// The editor's live length. It is a count of what a session will receive, not of what is
    /// stored: a lane fills its §11 char budget out of these, so the figure moves as the curator
    /// types rather than when they save.
    /// </summary>
    public static string CharacterCount(int length)
        => length == 1 ? "1 character" : $"{length:N0} characters";

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
