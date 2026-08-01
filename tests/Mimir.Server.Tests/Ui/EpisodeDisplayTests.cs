using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The §8.2 list's words, with no database anywhere near them (#94): every one of these is a pure
/// function of one summary, so these pins run on a machine with no Postgres — the machine where a
/// mistake in them would be made.
/// </summary>
public sealed class EpisodeDisplayTests
{
    [Fact]
    public void AnUnsealedEpisode_IsLive_WhateverItsDistillationColumnSays()
    {
        // Capture creates the row Pending and only Sealing enqueues (§6), so reading the column
        // first would mark every live session "pending".
        EpisodeDisplay.State(sealedAt: null, DistillationState.Pending).ShouldBe(EpisodeState.Live);
    }

    [Theory]
    [InlineData(DistillationState.Pending, EpisodeState.Pending)]
    [InlineData(DistillationState.Running, EpisodeState.Running)]
    [InlineData(DistillationState.Done, EpisodeState.Done)]
    [InlineData(DistillationState.Failed, EpisodeState.Failed)]
    public void ASealedEpisode_MarksWhereItsDistillationIs(
        DistillationState distillation, EpisodeState expected)
    {
        EpisodeDisplay.State(sealedAt: Sealed, distillation).ShouldBe(expected);
    }

    [Fact]
    public void OnlyTheRestingState_GoesUnmarked()
    {
        EpisodeDisplay.StateWord(EpisodeState.Live).ShouldBe("live");
        EpisodeDisplay.StateWord(EpisodeState.Pending).ShouldBe("pending");
        EpisodeDisplay.StateWord(EpisodeState.Running).ShouldBe("running");
        EpisodeDisplay.StateWord(EpisodeState.Failed).ShouldBe("failed");
        EpisodeDisplay.StateWord(EpisodeState.Done).ShouldBeNull();
    }

    [Fact]
    public void ALiveEpisode_SaysOnlyWhereItRuns_LeavingLiveToTheRowsOwnMark()
    {
        // "live" in the row's mark and "unsealed" here would be two words for one fact; the meta
        // line carries how a session ended, and this one has not.
        var live = Summary(sealReason: null) with { SealedAt = null };

        EpisodeDisplay.MetaLine(live).ShouldBe(@"C:\git\mimir");
    }

    [Fact]
    public void ARowAndTheDrillDown_WordOneSealTheSameWay()
    {
        var summary = Summary(sealReason: "logout");

        EpisodeDisplay.MetaLine(summary)
            .ShouldContain(EpisodeDisplay.StateLabel(summary.SealedAt, summary.SealReason));
    }

    [Fact]
    public void ASealedEpisode_NamesItsReasonAndWhatItProduced()
    {
        var summary = Summary(sealReason: "clear", wisdomCount: 2);

        EpisodeDisplay.MetaLine(summary).ShouldBe(@"C:\git\mimir · sealed · clear · 2 Wisdom");
    }

    [Fact]
    public void ADistilledEpisodeThatProducedNothing_SaysSoInWords()
    {
        // A quiet session and a session whose figure is simply absent must not read alike.
        var quiet = Summary(sealReason: "clear", wisdomCount: 0);

        EpisodeDisplay.MetaLine(quiet).ShouldBe(@"C:\git\mimir · sealed · clear · no Wisdom");
    }

    [Fact]
    public void AnEpisodeStillOwedDistillation_ClaimsNeither()
    {
        // Pending, running and failed have not been distilled, so there is no figure to state yet.
        foreach (var distillation in new[]
            { DistillationState.Pending, DistillationState.Running, DistillationState.Failed })
        {
            EpisodeDisplay.MetaLine(Summary(sealReason: "clear", distillation: distillation))
                .ShouldNotContain("Wisdom");
        }
    }

    [Fact]
    public void ACrashSweptEpisode_IsDistinguishableFromACleanExit()
    {
        var swept = Summary(sealReason: Episode.CrashSweptReason);

        EpisodeDisplay.MetaLine(swept).ShouldBe(@"C:\git\mimir · sealed · crash-swept · no Wisdom");
    }

    [Fact]
    public void ASealWithNoRecordedReason_SaysSo_RatherThanReadingUnsealed()
    {
        var summary = Summary(sealReason: null);

        EpisodeDisplay.MetaLine(summary).ShouldBe(@"C:\git\mimir · sealed · no reason · no Wisdom");
    }

    [Fact]
    public void OneWisdom_IsNotPluralised()
    {
        EpisodeDisplay.MetaLine(Summary(sealReason: "clear", wisdomCount: 1))
            .ShouldBe(@"C:\git\mimir · sealed · clear · 1 Wisdom");
    }

    [Fact]
    public void AFailedEpisode_NamesTheSweepsRecovery_SoFailedDoesNotReadTerminal()
    {
        var failed = Summary(sealReason: "clear", distillation: DistillationState.Failed);

        EpisodeDisplay.MetaLine(failed).ShouldBe(@"C:\git\mimir · sealed · clear · re-queued next sweep");
    }

    [Fact]
    public void AProducingFailure_NamesBoth()
    {
        var failed = Summary(sealReason: "clear", distillation: DistillationState.Failed, wisdomCount: 3);

        EpisodeDisplay.MetaLine(failed)
            .ShouldBe(@"C:\git\mimir · sealed · clear · 3 Wisdom · re-queued next sweep");
    }

    [Fact]
    public void TheEventCount_IsPluralisedAroundOne()
    {
        EpisodeDisplay.EventsLabel(0).ShouldBe("0 Events");
        EpisodeDisplay.EventsLabel(1).ShouldBe("1 Event");
        EpisodeDisplay.EventsLabel(23).ShouldBe("23 Events");
    }

    [Fact]
    public void TheStamp_RendersUtc_InTheDisplayFormat()
    {
        // A non-UTC offset: the format must convert rather than print local wall-clock time.
        var at = new DateTimeOffset(2026, 7, 26, 9, 20, 0, TimeSpan.FromHours(-5));

        EpisodeDisplay.Stamp(at).ShouldBe("2026-07-26 14:20 UTC");
    }

    [Theory]
    [InlineData(0, 0, 42, "42s")]
    [InlineData(0, 45, 0, "45m")]
    [InlineData(0, 59, 44, "59m")]
    [InlineData(1, 0, 0, "1h 00m")]
    [InlineData(1, 30, 0, "1h 30m")]
    [InlineData(23, 59, 0, "23h 59m")]
    // §4 crash-Seals only after a day idle, so the swept session — the common unsealed outcome —
    // is over a day long and must not come out as a three-digit hour count.
    [InlineData(25, 6, 0, "1d 01h")]
    [InlineData(73, 30, 0, "3d 01h")]
    public void ADuration_ReadsInTheLargestUnitItFills(int hours, int minutes, int seconds, string expected)
    {
        var started = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

        EpisodeDisplay.Duration(started, started.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds))
            .ShouldBe(expected);
    }

    [Fact]
    public void AnUnsealedEpisode_HasNoDurationToState()
    {
        // The session has not ended, so there is no span to report — the aside says so in words
        // rather than quietly counting up to now, which would be a figure nothing recorded.
        EpisodeDisplay.Duration(Sealed.AddHours(-1), sealedAt: null).ShouldBeNull();
    }

    [Fact]
    public void ASealStampedBeforeItsStart_ReadsAsNoTime_RatherThanNegative()
    {
        // Two hosts' clocks write these two columns, so skew is possible and a "-3s" would be a
        // reading of the clocks, not of the session.
        EpisodeDisplay.Duration(Sealed, Sealed.AddSeconds(-3)).ShouldBe("0s");
    }

    [Fact]
    public void AnUnsealedEpisode_IsNotInTheQueueAtAll_WhateverItsColumnSays()
    {
        // Capture creates the row Pending; Sealing is what enqueues (§6). Reading the column out
        // loud here would tell a curator a live session is waiting on a worker.
        EpisodeDisplay.DistillationPhrase(sealedAt: null, DistillationState.Pending)
            .ShouldBe("not queued — Sealing is what enqueues");
    }

    [Theory]
    [InlineData(DistillationState.Pending, "pending")]
    [InlineData(DistillationState.Running, "running")]
    [InlineData(DistillationState.Done, "done")]
    [InlineData(DistillationState.Failed, "failed")]
    public void ASealedEpisode_NamesItsDistillationColumn(DistillationState state, string expected)
    {
        EpisodeDisplay.DistillationPhrase(Sealed, state).ShouldBe(expected);
    }

    [Fact]
    public void AStreamInsideTheBound_IsWholeAndSaysNothingAboutIt()
    {
        // Nothing is withheld, so there is no bound to state and no control to offer.
        EpisodeDisplay.StreamBoundNote(EpisodeDisplay.StreamBound, expanded: false).ShouldBeNull();
        EpisodeDisplay.StreamToggleLabel(EpisodeDisplay.StreamBound, expanded: false).ShouldBeNull();
    }

    [Fact]
    public void ABoundedStream_SaysHowManyItIsShowingOfHowMany()
    {
        var total = EpisodeDisplay.StreamBound + 262;

        EpisodeDisplay.StreamBoundNote(total, expanded: false)
            .ShouldBe($"The first {EpisodeDisplay.StreamBound} of {total:N0} Events.");
        EpisodeDisplay.StreamToggleLabel(total, expanded: false).ShouldBe("Show the remaining 262");
    }

    [Fact]
    public void AnExpandedStream_SaysItIsWhole_AndOffersTheBoundBack()
    {
        var total = EpisodeDisplay.StreamBound + 262;

        EpisodeDisplay.StreamBoundNote(total, expanded: true).ShouldBe($"All {total:N0} Events.");
        EpisodeDisplay.StreamToggleLabel(total, expanded: true)
            .ShouldBe($"Show the first {EpisodeDisplay.StreamBound} only");
    }

    [Fact]
    public void OneEventPastTheBound_IsStillBounded()
    {
        // The straddling case: the bound holds at the first Event it actually withholds, not one
        // short of it, so a stream of bound+1 must not render whole and unannounced.
        EpisodeDisplay.StreamBoundNote(EpisodeDisplay.StreamBound + 1, expanded: false).ShouldNotBeNull();
        EpisodeDisplay.StreamToggleLabel(EpisodeDisplay.StreamBound + 1, expanded: false)
            .ShouldBe("Show the remaining 1");
    }

    [Theory]
    [InlineData(6, "6 h")]
    [InlineData(1, "1 h")]
    public void AConfiguredInterval_ReadsInWholeHours(int hours, string expected)
    {
        EpisodeDisplay.Hours(TimeSpan.FromHours(hours)).ShouldBe(expected);
    }

    [Fact]
    public void AnUrlWithNoEventAnchor_AnchorsNothing()
    {
        EpisodeDisplay.AnchoredEvent("http://localhost/projects/p/episodes/e").ShouldBeNull();
        EpisodeDisplay.AnchoredEvent("http://localhost/projects/p/episodes/e#top").ShouldBeNull();
        EpisodeDisplay.AnchoredEvent("http://localhost/projects/p/episodes/e#event-nonsense").ShouldBeNull();
    }

    [Fact]
    public void AProvenanceLink_AnchorsTheEventItNames()
    {
        // The shape WisdomSurface writes for §8.1's "open the Episode at the Event itself".
        var eventId = Guid.NewGuid();

        EpisodeDisplay.AnchoredEvent($"http://localhost/projects/p/episodes/e#event-{eventId}")
            .ShouldBe(eventId);
    }

    [Fact]
    public void TheAnchorTheLinkWrites_IsTheOneTheStreamCarries_AndTheReaderOpens()
    {
        // The round trip the three sites used to spell separately: WisdomSurface's href, the
        // stream's DOM id, and this reader. A mismatch never fails to build — the link just stops
        // landing — so the loop is closed here instead. The literal shape stays pinned above.
        var eventId = Guid.NewGuid();

        EpisodeDisplay.EventAnchorHref(eventId)
            .ShouldBe("#" + EpisodeDisplay.EventAnchorId(eventId));
        EpisodeDisplay.AnchoredEvent(
            $"http://localhost/projects/p/episodes/e{EpisodeDisplay.EventAnchorHref(eventId)}")
            .ShouldBe(eventId);
    }

    [Fact]
    public void AFailedDistillation_SaysItWasAttempted_NotThatItNeverRan()
    {
        // The aside beside this note reads "failed" and promises a re-queue. Saying "has not been
        // distilled" here would leave one Episode described two ways on one screen.
        var note = EpisodeDisplay.NothingProducedNote(Sealed, DistillationState.Failed);

        note.ShouldContain("failed");
        note.ShouldContain("re-queues");
        note.ShouldNotContain("has not been distilled");
    }

    [Fact]
    public void ADoneEpisodeThatProducedNothing_SaysTheEmptinessIsSettled()
    {
        // §6 prefers no candidate to a weak one, so this is an outcome rather than a wait.
        EpisodeDisplay.NothingProducedNote(Sealed, DistillationState.Done)
            .ShouldContain("no candidate over a weak one");
    }

    [Theory]
    [InlineData(DistillationState.Pending)]
    [InlineData(DistillationState.Running)]
    public void AnEpisodeStillOwedDistillation_SaysTheFigureIsNotInYet(DistillationState state)
    {
        EpisodeDisplay.NothingProducedNote(Sealed, state).ShouldContain("Nothing yet");
    }

    [Fact]
    public void AnUnsealedEpisode_IsNotDescribedByItsColumn_HereEither()
    {
        // Routed through State, the one rule: the column reads Pending from the moment capture
        // creates the row, so a live session must not read as a distillation that ran.
        EpisodeDisplay.NothingProducedNote(sealedAt: null, DistillationState.Done)
            .ShouldContain("Nothing yet");
        EpisodeDisplay.NothingProducedNote(sealedAt: null, DistillationState.Failed)
            .ShouldContain("Nothing yet");
    }

    [Fact]
    public void AnAnchorTheBoundWouldWithhold_OpensTheStreamWhole()
    {
        // Otherwise the Provenance link lands on an element that is not in the page at all.
        var events = Enumerable.Range(0, EpisodeDisplay.StreamBound + 3).Select(_ => Guid.NewGuid()).ToList();

        EpisodeDisplay.AnchorIsPastTheBound(events, $"http://x/#event-{events[^1]}").ShouldBeTrue();
    }

    [Fact]
    public void AnAnchorInsideTheBound_LeavesTheStreamAsItIs()
    {
        var events = Enumerable.Range(0, EpisodeDisplay.StreamBound + 3).Select(_ => Guid.NewGuid()).ToList();

        EpisodeDisplay.AnchorIsPastTheBound(events, $"http://x/#event-{events[EpisodeDisplay.StreamBound - 1]}")
            .ShouldBeFalse();
        EpisodeDisplay.AnchorIsPastTheBound(events, "http://x/projects/p/episodes/e").ShouldBeFalse();
        EpisodeDisplay.AnchorIsPastTheBound(events, $"http://x/#event-{Guid.NewGuid()}").ShouldBeFalse();
    }

    [Fact]
    public void ASealWithoutAReason_IsWordedTheSameWayEverywhere()
    {
        // The aside states the Seal reason on its own line; the row states it inside MetaLine. One
        // phrase, so the two screens cannot drift.
        EpisodeDisplay.SealPhrase(sealReason: null).ShouldBe("no reason");
        EpisodeDisplay.MetaLine(Summary(sealReason: null))
            .ShouldContain(EpisodeDisplay.SealPhrase(sealReason: null));
    }

    /// <summary>
    /// All four mappings, not just the one another suite happened to reach: the words are
    /// CONTEXT.md's own, from the Event entry, and a curator reading a Wisdom's Provenance is told
    /// what a moment was rather than what a hook is called.
    /// </summary>
    [Fact]
    public void EveryEventType_IsNamedInWords_NeverByItsHook()
    {
        EpisodeDisplay.EventWord(EventType.UserPromptSubmit).ShouldBe("a prompt");
        EpisodeDisplay.EventWord(EventType.PostToolUse).ShouldBe("tool activity");
        EpisodeDisplay.EventWord(EventType.Stop).ShouldBe("an assistant message");
        EpisodeDisplay.EventWord(EventType.Remember).ShouldBe("a deliberate save");

        // The mapping's default arm answers "a deliberate save", so a fifth §3 Event type would
        // be worded as one silently. Named here so adding one is a decision rather than a default.
        Enum.GetValues<EventType>().Length.ShouldBe(4);
    }

    private static readonly DateTimeOffset Sealed = new(2026, 7, 26, 14, 20, 0, TimeSpan.Zero);

    /// <summary>A Sealed summary; the one live case unseals it with a <c>with</c> expression.</summary>
    private static EpisodeSummary Summary(
        string? sealReason = "clear",
        DistillationState distillation = DistillationState.Done,
        int wisdomCount = 0)
        => new(
            Guid.NewGuid(),
            "sess-1",
            Sealed.AddHours(-1),
            Sealed,
            sealReason,
            @"C:\git\mimir",
            EventCount: 23,
            distillation,
            wisdomCount);
}
