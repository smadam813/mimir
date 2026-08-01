using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

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

    [Fact]
    public void AProvenanceLinkingToNothing_StillReadsAsSomething()
    {
        var link = Empty();

        WisdomDisplay.ProvenanceTitle(link).ShouldBe("An unrecorded source");
        WisdomDisplay.ProvenanceDetail(link).ShouldBe("nothing this row still points at");
    }

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

    [Fact]
    public void TheCauseBadge_ReadsMergedAsReinforced_AndEveryOtherCauseAsItself()
    {
        WisdomDisplay.CauseWord(WisdomVersionCause.Merged).ShouldBe("reinforced");
        WisdomDisplay.CauseWord(WisdomVersionCause.Distilled).ShouldBe("distilled");
        WisdomDisplay.CauseWord(WisdomVersionCause.Adjudicated).ShouldBe("adjudicated");
        WisdomDisplay.CauseWord(WisdomVersionCause.Edited).ShouldBe("edited");
    }

    [Fact]
    public void TheLegend_DefinesEveryCauseTheDomainHas()
    {
        WisdomDisplay.CauseLegend.Select(gloss => gloss.Word)
            .ShouldBe(["distilled", "reinforced", "adjudicated", "edited"]);
        WisdomDisplay.CauseLegend.Select(gloss => gloss.Meaning).Distinct()
            .Count().ShouldBe(WisdomDisplay.CauseLegend.Count);
    }

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

    [Fact]
    public void TheFirstVersion_HasNothingToDifferFrom_AndReadsPlain()
    {
        WisdomDisplay.Diff(previous: null, "the first wording")
            .ShouldBe([new TextRun(TextChange.Kept, "the first wording")]);
    }

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

        WisdomDisplay.WithPendingEdit(
                WisdomDisplay.Chain(
                    [Version(4, "the head version's words", WisdomVersionCause.Merged)]),
                current: "what the Wisdom says",
                draft: "what the Wisdom says")
            .ShouldAllBe(version => !version.Pending);
    }

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

    private static string[] BareWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

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
