using Mimir.Server.Recall;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The Brief's growth tripwire (#72), threshold by threshold. Pure arithmetic over an elapsed span
/// and a row count, so it is pinned here rather than through a compose — the size threshold is
/// 25,001 Wisdom rows, and seeding those into Postgres costs minutes of HNSW index maintenance for
/// a comparison that touches no SQL. <see cref="BriefServiceTests"/> pins the wiring: that a real
/// compose hands this its own elapsed time and its own candidate count, and that the line it
/// returns reaches the Brief.
/// </summary>
public class BriefTripwireTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void InsideBothThresholds_FiresNeitherChannel()
    {
        var log = new CapturedLog();

        BriefTripwire.Fire(log, Quick, BriefTripwire.CandidateWarnAbove).ShouldBeNull();

        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void AtTheWallClockThreshold_FiresBothChannels()
    {
        var log = new CapturedLog();

        var notice = BriefTripwire.Fire(log, BriefTripwire.ComposeWarnAfter, candidates: 12);

        notice.ShouldNotBeNull();
        log.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void JustUnderTheWallClockThreshold_FiresNeitherChannel()
    {
        var log = new CapturedLog();

        BriefTripwire.Fire(
            log, BriefTripwire.ComposeWarnAfter - TimeSpan.FromMilliseconds(1), candidates: 12)
            .ShouldBeNull();

        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void OneRowPastTheSizeThreshold_FiresBothChannels_EvenWhenTheComposeWasInstant()
    {
        var log = new CapturedLog();

        var notice = BriefTripwire.Fire(log, Quick, BriefTripwire.CandidateWarnAbove + 1);

        // The size leg exists precisely for the machine fast enough to walk a corpus this large
        // inside the wall-clock threshold — the hardware is what makes that true, and the next
        // machine's may not.
        notice.ShouldNotBeNull();
        log.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void TheNoticeIsOneLine_NamingTheSeconds_TheRowCount_AndTheIssue()
    {
        var notice = BriefTripwire.Fire(
            new CapturedLog(), TimeSpan.FromMilliseconds(2149), candidates: 48_102);

        notice.ShouldBe("⚠ Mimir: Brief composed in 2.1s (budget 3s); "
            + "ambient set 48,102 rows — see #72.\n");
    }

    [Fact]
    public void TheWarningLog_NamesTheSameFactsAsTheNotice()
    {
        var log = new CapturedLog();

        BriefTripwire.Fire(log, TimeSpan.FromMilliseconds(2149), candidates: 48_102);

        var warning = log.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("2.1s");
        warning.ShouldContain("48102");
        warning.ShouldContain("#72");
    }
}
