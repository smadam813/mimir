using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The §8.1 detail's words and its one piece of arithmetic, with no database anywhere near them
/// (#92): each is a pure function of one Provenance row or one figure, so these pins run — and can
/// fail — on a machine with no Postgres reachable, which is the machine where a mistake in them
/// would be made.
/// </summary>
public sealed class WisdomDisplayTests
{
    private static readonly DateTimeOffset EventAt = new(2026, 7, 24, 8, 12, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EpisodeAt = new(2026, 7, 24, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AnEventProvenance_IsNamedByTheMomentItself_NotBySessionId()
    {
        var link = FromEvent();

        WisdomDisplay.ProvenanceTitle(link).ShouldBe("2026-07-24 08:12 UTC");
        WisdomDisplay.ProvenanceDetail(link).ShouldBe("tool activity in ~/src/mimir · Event #4");
    }

    /// <summary>
    /// A candidate that named no Events links to the session as a whole (§6), so the row falls
    /// back to when that session started rather than reading blank.
    /// </summary>
    [Fact]
    public void AnEpisodeProvenance_IsNamedByWhenTheSessionStarted()
    {
        var link = FromEvent() with { EventId = null, EventSeq = null, EventType = null, EventAt = null };

        WisdomDisplay.ProvenanceTitle(link).ShouldBe("2026-07-24 07:30 UTC");
        WisdomDisplay.ProvenanceDetail(link).ShouldBe("the session in ~/src/mimir");
    }

    [Fact]
    public void AHarvestProvenance_IsNamedByWhereItWasHarvestedFrom()
    {
        var link = Empty() with
        {
            HarvestedItemId = Guid.NewGuid(),
            HarvestedPath = "~/.claude/memory/mimir.md",
        };

        WisdomDisplay.ProvenanceTitle(link).ShouldBe("Harvested from auto-memory");
        WisdomDisplay.ProvenanceDetail(link).ShouldBe("~/.claude/memory/mimir.md");
    }

    /// <summary>
    /// The guard, not a fourth shape: the gate writes no all-null Provenance row and the cascade
    /// removes rows rather than blanking them, so this pins that a renderer handed one anyway says
    /// something rather than throwing. It is deliberately not a claim that such a row exists.
    /// </summary>
    [Fact]
    public void AProvenanceLinkingToNothing_StillReadsAsSomething()
    {
        var link = Empty();

        WisdomDisplay.ProvenanceTitle(link).ShouldBe("An unrecorded source");
        WisdomDisplay.ProvenanceDetail(link).ShouldBe("nothing this row still points at");
    }

    /// <summary>
    /// The list's closing line promises the rows open an Episode, so it renders only where one
    /// does: a Wisdom harvested out of auto-memory has no session behind it to open.
    /// </summary>
    [Fact]
    public void TheProvenanceNote_PromisesTheEpisodeOnlyWhereThereIsOne()
    {
        WisdomDisplay.ProvenanceNote([FromEvent()]).ShouldNotBeNull();
        WisdomDisplay.ProvenanceNote(
            [Empty() with { HarvestedItemId = Guid.NewGuid(), HarvestedPath = "~/mem.md" }])
            .ShouldBeNull();
        WisdomDisplay.ProvenanceNote([]).ShouldBeNull();
    }

    [Fact]
    public void TheEditorCountsWhatASessionWouldReceive_AndTheAsideNamesItsUnit()
    {
        WisdomDisplay.CharacterCount(1).ShouldBe("1 character");
        WisdomDisplay.CharacterCount(1_580).ShouldBe("1,580 characters");
        WisdomDisplay.ReinforcementUnit(1).ShouldBe("confirmation");
        WisdomDisplay.ReinforcementUnit(4).ShouldBe("confirmations");
        WisdomDisplay.VersionCount(1).ShouldBe("1 version");
        WisdomDisplay.VersionCount(5).ShouldBe("5 versions");
        WisdomDisplay.RetireHint(retired: false).ShouldContain("reversible");
        WisdomDisplay.RetireHint(retired: true).ShouldContain("Unretire");
    }

    /// <summary>
    /// The badge names the Admission outcome, the enum names the text operation the gate performed,
    /// and <c>Merged</c> is the one place the two want different words (#93). Every other cause
    /// reads as its own member name, so the mapping is a mapping of one rather than a table that
    /// can fall out of step with §3.
    /// </summary>
    [Fact]
    public void TheCauseBadge_ReadsMergedAsReinforced_AndEveryOtherCauseAsItself()
    {
        WisdomDisplay.CauseWord(WisdomVersionCause.Merged).ShouldBe("reinforced");
        WisdomDisplay.CauseWord(WisdomVersionCause.Distilled).ShouldBe("distilled");
        WisdomDisplay.CauseWord(WisdomVersionCause.Adjudicated).ShouldBe("adjudicated");
        WisdomDisplay.CauseWord(WisdomVersionCause.Edited).ShouldBe("edited");
    }

    /// <summary>
    /// The legend claims to define the badges above it, so it is built from the enum rather than
    /// listed: every cause §3 has, in §3's own order, each with words of its own. The drawn design
    /// named three — this is the pin that keeps <c>adjudicated</c> from going missing again.
    /// </summary>
    [Fact]
    public void TheLegend_DefinesEveryCauseTheDomainHas()
    {
        WisdomDisplay.CauseLegend.Select(gloss => gloss.Word)
            .ShouldBe(["distilled", "reinforced", "adjudicated", "edited"]);
        WisdomDisplay.CauseLegend.Select(gloss => gloss.Meaning).Distinct()
            .Count().ShouldBe(WisdomDisplay.CauseLegend.Count);
    }

    /// <summary>
    /// What the chain's default view is for: one reworded clause marked inside a version that is
    /// otherwise the version below it. The two texts share a prefix and a suffix that each occur
    /// once, and the words that move share none of their spelling with the words that stay, so
    /// nothing but the diff itself can produce this list — the substitution is the only term the
    /// fixture leaves free to move. Removed before added, because the pair reads as one rewording
    /// rather than as a deletion beside an unrelated arrival.
    /// </summary>
    [Fact]
    public void TheDiff_MarksTheWordsThatWent_AndThenTheWordsThatArrived()
    {
        var runs = WisdomDisplay.Diff(
            "so a cascaded delete skips interceptors.",
            "so a cascaded delete never fires SaveChanges interceptors.");

        runs.ShouldBe([
            new TextRun(TextChange.Kept, "so a cascaded delete "),
            new TextRun(TextChange.Removed, "skips "),
            new TextRun(TextChange.Added, "never fires SaveChanges "),
            new TextRun(TextChange.Kept, "interceptors."),
        ]);
    }

    /// <summary>
    /// A row draws its own version's words, so what it draws has to be exactly those words: a diff
    /// that dropped or invented a character would show a curator a text no version ever said, and
    /// the whitespace is as much of that text as the letters. The removed runs are held to the
    /// other side the same way, word for word. Four shapes plus both empties, because a
    /// substitution, a pure insertion, a pure deletion and a wholesale rewrite take four different
    /// paths through the walk.
    /// </summary>
    [Theory]
    [InlineData("alpha beta gamma", "alpha delta gamma")]
    [InlineData("alpha gamma", "alpha beta beta gamma")]
    [InlineData("alpha beta gamma", "alpha")]
    [InlineData("alpha beta", "gamma delta")]
    [InlineData("", "alpha beta")]
    [InlineData("alpha beta", "")]
    public void TheDiff_DrawsExactlyTheNewText_AndAccountsForEveryWordOfTheOld(
        string previous, string current)
    {
        var runs = WisdomDisplay.Diff(previous, current);

        string.Concat(runs.Where(r => r.Change is not TextChange.Removed).Select(r => r.Text))
            .ShouldBe(current);
        BareWords(string.Concat(runs.Where(r => r.Change is not TextChange.Added).Select(r => r.Text)))
            .ShouldBe(BareWords(previous));
    }

    /// <summary>
    /// A struck run has to stay clear of the words beside it: a word carries the whitespace after
    /// it, so a removal inside a text separates itself, but one at either edge of the row's own
    /// text has nothing between it and its neighbour and renders as one fused word. All three
    /// edges, because each takes the separator from a different side — a substitution ending both
    /// texts, a deletion opening the old one, and a deletion running off the end of the new one.
    /// </summary>
    [Fact]
    public void TheDiff_KeepsAStruckRunClearOfTheWordsBesideIt()
    {
        WisdomDisplay.Diff("alpha gamma", "alpha delta").ShouldBe([
            new TextRun(TextChange.Kept, "alpha "),
            new TextRun(TextChange.Removed, "gamma "),
            new TextRun(TextChange.Added, "delta"),
        ]);

        WisdomDisplay.Diff("beta alpha", "alpha").ShouldBe([
            new TextRun(TextChange.Removed, "beta "),
            new TextRun(TextChange.Kept, "alpha"),
        ]);

        WisdomDisplay.Diff("alpha beta gamma", "alpha").ShouldBe([
            new TextRun(TextChange.Kept, "alpha"),
            new TextRun(TextChange.Removed, " beta gamma"),
        ]);
    }

    /// <summary>
    /// The foot of the chain has nothing to be different from, so it reads plain: drawn as
    /// wholesale arrival it would tell a curator a model added words to something, when what it
    /// did was write the line.
    /// </summary>
    [Fact]
    public void TheFirstVersion_HasNothingToDifferFrom_AndReadsPlain()
    {
        WisdomDisplay.Diff(previous: null, "the first wording")
            .ShouldBe([new TextRun(TextChange.Kept, "the first wording")]);
    }

    /// <summary>
    /// Past the bound the diff says the text changed rather than how. The walk is quadratic in the
    /// two texts and the pane draws one per version, so a single pathological Wisdom would
    /// otherwise take the render down with it. Both sides of the constant, because a bound that
    /// fires early is a chain that stops explaining itself: at exactly the bound the words are
    /// still marked one by one.
    /// </summary>
    [Fact]
    public void TheDiff_PastItsWordBound_SaysTheWholeTextChanged()
    {
        var words = Enumerable.Range(0, WisdomDisplay.DiffWordBound)
            .Select(word => $"w{word}").ToArray();
        var atBound = string.Join(' ', words);
        var reworded = string.Join(' ', words[..^1].Append("reworded"));

        WisdomDisplay.Diff(atBound, reworded).ShouldBe([
            new TextRun(TextChange.Kept, string.Join(' ', words[..^1]) + " "),
            new TextRun(TextChange.Removed, words[^1] + " "),
            new TextRun(TextChange.Added, "reworded"),
        ]);

        var pastBound = atBound + " overrun";
        WisdomDisplay.Diff(pastBound, reworded + " overrun").ShouldBe([
            new TextRun(TextChange.Removed, pastBound + " "),
            new TextRun(TextChange.Added, reworded + " overrun"),
        ]);
    }

    /// <summary>
    /// Newest first, and the chain's own doing rather than its caller's ORDER BY: each row is
    /// diffed against the row below it, so a chain handed back the other way up draws every
    /// version's arrivals as departures, silently and on every screen. Seeded oldest-first — the
    /// order this must not return — so a dropped sort cannot pass by accident.
    /// </summary>
    [Fact]
    public void TheChain_ReadsNewestFirst_AndDiffsEachVersionAgainstTheOneBelowIt()
    {
        var chain = WisdomDisplay.Chain(
            [
                Version(1, "alpha beta", WisdomVersionCause.Distilled),
                Version(2, "alpha gamma", WisdomVersionCause.Merged),
                Version(3, "alpha delta", WisdomVersionCause.Edited),
            ]);

        chain.Select(v => (v.Version, v.Cause)).ShouldBe([
            (3, WisdomVersionCause.Edited),
            (2, WisdomVersionCause.Merged),
            (1, WisdomVersionCause.Distilled),
        ]);
        chain.ShouldAllBe(v => !v.Pending && v.At != null);

        chain[0].Changed.ShouldBe([
            new TextRun(TextChange.Kept, "alpha "),
            new TextRun(TextChange.Removed, "gamma "),
            new TextRun(TextChange.Added, "delta"),
        ]);
        chain[1].Changed.ShouldBe([
            new TextRun(TextChange.Kept, "alpha "),
            new TextRun(TextChange.Removed, "beta "),
            new TextRun(TextChange.Added, "gamma"),
        ]);
        chain[2].Changed.ShouldBe([new TextRun(TextChange.Kept, "alpha beta")]);
    }

    /// <summary>
    /// The draft in the editor is drawn as the version it would become, above the head, so a
    /// curator reads their own rewording against what stands without saving to see it. It carries
    /// no timestamp, because the gate has written none, and it carries the trimmed text, because
    /// trimmed is what the gate would write.
    /// </summary>
    [Fact]
    public void AnUnsavedDraft_HeadsTheChain_AsTheVersionItWouldBecome()
    {
        var chain = WisdomDisplay.WithPendingEdit(
            WisdomDisplay.Chain([Version(4, "alpha beta", WisdomVersionCause.Merged)]),
            current: "alpha beta",
            draft: "  alpha gamma  ");

        chain.Select(v => (v.Version, v.Pending)).ShouldBe([(5, true), (4, false)]);
        chain[0].At.ShouldBeNull();
        chain[0].Cause.ShouldBe(WisdomVersionCause.Edited);
        chain[0].Text.ShouldBe("alpha gamma");
        chain[0].Changed.ShouldBe([
            new TextRun(TextChange.Kept, "alpha "),
            new TextRun(TextChange.Removed, "beta "),
            new TextRun(TextChange.Added, "gamma"),
        ]);
    }

    /// <summary>
    /// Which text the pending row is measured against: the one the caller hands in — the same row
    /// the Save button reads — and not the head version's, which only says the same thing because
    /// every rewrite goes through the gate. The fixture parts the two to say so, and the state it
    /// describes is one production does not reach.
    /// </summary>
    [Fact]
    public void AnUnsavedDraft_IsMeasuredAgainstWhatTheWisdomSays_NotAgainstItsHeadVersion()
    {
        var chain = WisdomDisplay.WithPendingEdit(
            WisdomDisplay.Chain([Version(4, "the head version's words", WisdomVersionCause.Merged)]),
            current: "what the Wisdom says",
            draft: "what the Wisdom said");

        chain[0].Changed.ShouldBe([
            new TextRun(TextChange.Kept, "what the Wisdom "),
            new TextRun(TextChange.Removed, "says "),
            new TextRun(TextChange.Added, "said"),
        ]);

        // And the same for whether there is an edit here at all: a draft already saying what the
        // Wisdom says is a no-op, whatever the head version happens to hold.
        WisdomDisplay.WithPendingEdit(
                WisdomDisplay.Chain(
                    [Version(4, "the head version's words", WisdomVersionCause.Merged)]),
                current: "what the Wisdom says",
                draft: "what the Wisdom says")
            .ShouldAllBe(version => !version.Pending);
    }

    /// <summary>
    /// A draft that would save nothing is not a version. The gate's own no-op set decides, the
    /// same single statement the Save button beside the chain reads, so the pending row and the
    /// enabled button can never disagree about whether there is an edit here at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alpha beta")]
    [InlineData("  alpha beta  ")]
    public void ADraftThatWouldSaveNothing_IsNotAVersion(string draft)
    {
        WisdomDisplay.WithPendingEdit(
                WisdomDisplay.Chain([Version(4, "alpha beta", WisdomVersionCause.Merged)]),
                current: "alpha beta",
                draft)
            .Select(v => v.Version).ShouldBe([4]);
    }

    private static readonly Guid WisdomId = Guid.NewGuid();

    private static WisdomVersion Version(int version, string text, WisdomVersionCause cause)
        => new()
        {
            WisdomId = WisdomId,
            Version = version,
            Text = text,
            CreatedAt = EpisodeAt.AddHours(version),
            Cause = cause,
        };

    /// <summary>The words of a text, whitespace discarded — what a run must still account for.</summary>
    private static string[] BareWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The Recall note carries the unmarked remainder and says what a mark is left on, because the
    /// two figures beside it sit under a Wisdom and would otherwise read as verdicts on it (§9).
    /// </summary>
    [Fact]
    public void TheRecallNote_CountsTheUnjudgedEntries_AndSaysWhatAMarkIsLeftOn()
    {
        var recalled = new WisdomRecall(
            [
                new LaneCount(InjectionLane.Brief, 2),
                new LaneCount(InjectionLane.Prompt, 3),
                new LaneCount(InjectionLane.Mcp, 1),
            ],
            MarkedUseful: 2,
            MarkedNoise: 1);

        recalled.Injections.ShouldBe(6);
        recalled.Unmarked.ShouldBe(3);

        // Whole, not a phrase out of it: the sentence has to name the total it is a remainder of,
        // or "still unmarked" is a figure with nothing behind it.
        WisdomDisplay.RecallNote(recalled).ShouldBe(
            "6 injections have carried this line, across every Project that recalled it, whole "
            + "history; 3 still unmarked. A mark is left on an injection as a whole (§9), so the "
            + "two figures above count entries this line rode in rather than verdicts on the line "
            + "itself.");

        WisdomDisplay.RecallNote(new WisdomRecall([new LaneCount(InjectionLane.Brief, 1)], 1, 0))
            .ShouldStartWith("1 injection has carried this line");

        WisdomDisplay.RecallNote(
                new WisdomRecall([new LaneCount(InjectionLane.Brief, 0)], 0, 0))
            .ShouldBe("No injection has carried this line yet, so nothing here has judged it.");
    }

    /// <summary>
    /// The bar is a fixed row of segments, so it has to stop somewhere: eleven confirmations light
    /// every segment and no more, and none lights none. Both edges, because a bar that ran past its
    /// row and a bar that never filled would both be drawn by the same missing clamp.
    /// </summary>
    [Fact]
    public void TheReinforcementBar_FillsOneSegmentPerConfirmation_AndStopsAtItsWidth()
    {
        WisdomDisplay.ReinforcementFilled(0).ShouldBe(0);
        WisdomDisplay.ReinforcementFilled(4).ShouldBe(4);
        WisdomDisplay.ReinforcementFilled(WisdomDisplay.ReinforcementBarSegments)
            .ShouldBe(WisdomDisplay.ReinforcementBarSegments);
        WisdomDisplay.ReinforcementFilled(WisdomDisplay.ReinforcementBarSegments + 3)
            .ShouldBe(WisdomDisplay.ReinforcementBarSegments);
    }

    /// <summary>A row linking to nothing at all — the shape the schema never writes.</summary>
    private static ProvenanceEntry Empty() => new(
        Guid.NewGuid(),
        EpisodeId: null,
        EpisodeProjectId: null,
        EpisodeCwd: null,
        EpisodeStartedAt: null,
        EventId: null,
        EventSeq: null,
        EventType: null,
        EventAt: null,
        HarvestedItemId: null,
        HarvestedPath: null);

    /// <summary>
    /// The editor's Save is disabled on the two no-ops the curator can see coming (§8.1) — the
    /// gate's own rule is unchanged, and a real rewording is never the one held back. The last case
    /// is that last clause: the gate trims the draft and compares it against the stored text as
    /// stored, so a stored text carrying whitespace has an edit that would land.
    /// </summary>
    [Fact]
    public void SavingIsPointless_OnBlankTextAndOnTextThatAlreadySaysThis()
    {
        WisdomDisplay.UnsavableReason("   ", "the current wording").ShouldNotBeNull();
        WisdomDisplay.UnsavableReason("", "the current wording").ShouldNotBeNull();
        WisdomDisplay.UnsavableReason(
            "  the current wording  ",
            "the current wording").ShouldNotBeNull("the gate trims the draft before it compares");
        WisdomDisplay.UnsavableReason("a new wording", "the current wording").ShouldBeNull();
        WisdomDisplay.UnsavableReason("the current wording", "the current wording  ").ShouldBeNull();
    }

    /// <summary>
    /// The paragraph beside the Save button says what the gate will do, so it is pinned to the
    /// gate's own terms: the version it appends, the cause it appends it under, and the two things
    /// it leaves alone. Held here rather than in markup for the reason the class doc gives.
    /// </summary>
    [Fact]
    public void TheEditorExplains_WhatTheGateWillDo_InTheGatesOwnTerms()
    {
        var explained = WisdomDisplay.EditExplanation(nextVersion: 5, reinforcement: 3);

        explained.ShouldBe(
            "Saving goes through the Merge Gate: it appends v5 · cause=edited, re-embeds the new "
            + "text, and waits behind any in-flight Admission batch. Reinforcement stays ×3 and "
            + "recency does not move — an edit rewords, it does not confirm (§6).");
    }

    private static ProvenanceEntry FromEvent() => new(
        Guid.NewGuid(),
        EpisodeId: Guid.NewGuid(),
        EpisodeProjectId: Guid.NewGuid(),
        EpisodeCwd: "~/src/mimir",
        EpisodeStartedAt: EpisodeAt,
        EventId: Guid.NewGuid(),
        EventSeq: 4,
        EventType: EventType.PostToolUse,
        EventAt: EventAt,
        HarvestedItemId: null,
        HarvestedPath: null);
}
