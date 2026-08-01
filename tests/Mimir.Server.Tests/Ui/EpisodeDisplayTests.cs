using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

public sealed class EpisodeDisplayTests
{
    [Fact]
    public void AnUnsealedEpisode_IsLive_WhateverItsDistillationColumnSays()
    {
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
        var quiet = Summary(sealReason: "clear", wisdomCount: 0);

        EpisodeDisplay.MetaLine(quiet).ShouldBe(@"C:\git\mimir · sealed · clear · no Wisdom");
    }

    [Fact]
    public void AnEpisodeStillOwedDistillation_ClaimsNeither()
    {
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
        EpisodeDisplay.Duration(Sealed.AddHours(-1), sealedAt: null).ShouldBeNull();
    }

    [Fact]
    public void ASealStampedBeforeItsStart_ReadsAsNoTime_RatherThanNegative()
    {
        EpisodeDisplay.Duration(Sealed, Sealed.AddSeconds(-3)).ShouldBe("0s");
    }

    [Fact]
    public void AnUnsealedEpisode_IsNotInTheQueueAtAll_WhateverItsColumnSays()
    {
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
        var eventId = Guid.NewGuid();

        EpisodeDisplay.AnchoredEvent($"http://localhost/projects/p/episodes/e#event-{eventId}")
            .ShouldBe(eventId);
    }

    [Fact]
    public void TheAnchorTheLinkWrites_IsTheOneTheStreamCarries_AndTheReaderOpens()
    {
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
        var note = EpisodeDisplay.NothingProducedNote(Sealed, DistillationState.Failed);

        note.ShouldContain("failed");
        note.ShouldContain("re-queues");
        note.ShouldNotContain("has not been distilled");
    }

    [Fact]
    public void ADoneEpisodeThatProducedNothing_SaysTheEmptinessIsSettled()
    {
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
        EpisodeDisplay.NothingProducedNote(sealedAt: null, DistillationState.Done)
            .ShouldContain("Nothing yet");
        EpisodeDisplay.NothingProducedNote(sealedAt: null, DistillationState.Failed)
            .ShouldContain("Nothing yet");
    }

    [Fact]
    public void AnAnchorTheBoundWouldWithhold_OpensTheStreamWhole()
    {
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
        EpisodeDisplay.SealPhrase(sealReason: null).ShouldBe("no reason");
        EpisodeDisplay.MetaLine(Summary(sealReason: null))
            .ShouldContain(EpisodeDisplay.SealPhrase(sealReason: null));
    }

    private static readonly DateTimeOffset Sealed = new(2026, 7, 26, 14, 20, 0, TimeSpan.Zero);

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
