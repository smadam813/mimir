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
    public void ALiveEpisode_SaysItIsUnsealed_WithNoWisdomFigureYet()
    {
        var live = Summary(sealReason: null) with { SealedAt = null };

        EpisodeDisplay.MetaLine(live).ShouldBe(@"C:\git\mimir · unsealed");
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
