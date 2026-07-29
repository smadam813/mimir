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
        WisdomDisplay.RetireHint(retired: false).ShouldContain("reversible");
        WisdomDisplay.RetireHint(retired: true).ShouldContain("Unretire");
    }

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
