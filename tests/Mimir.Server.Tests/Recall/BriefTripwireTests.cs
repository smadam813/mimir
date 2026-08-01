using Mimir.Server.Recall;

namespace Mimir.Server.Tests.Recall;

public class BriefTripwireTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void Fire_InsideBothThresholds_FiresNeitherChannel()
    {
        var log = new CapturedLog();

        BriefTripwire.Fire(log, Quick, BriefTripwire.CandidateWarnAbove).ShouldBeNull();

        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Fire_AtTheWallClockThreshold_FiresNeitherChannel()
    {
        var log = new CapturedLog();

        BriefTripwire.Fire(log, BriefTripwire.ComposeWarnAfter, candidates: 12).ShouldBeNull();

        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Fire_OneTickPastTheWallClockThreshold_FiresBothChannels()
    {
        var log = new CapturedLog();

        var notice = BriefTripwire.Fire(
            log, BriefTripwire.ComposeWarnAfter + TimeSpan.FromMilliseconds(1), candidates: 12);

        notice.ShouldNotBeNull();
        log.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void Fire_OneRowPastTheSizeThreshold_FiresBothChannels_EvenWhenTheComposeWasInstant()
    {
        var log = new CapturedLog();

        var notice = BriefTripwire.Fire(log, Quick, BriefTripwire.CandidateWarnAbove + 1);

        notice.ShouldNotBeNull();
        log.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void Fire_TheNoticeIsOneLine_NamingTheSeconds_TheRowCount_AndTheIssue()
    {
        var notice = BriefTripwire.Fire(
            new CapturedLog(), TimeSpan.FromMilliseconds(2149), candidates: 48_102);

        notice.ShouldBe("⚠ Mimir: Brief composed in 2.1s (budget 3s); "
            + "ambient set 48,102 rows — see #72.\n");
    }

    [Fact]
    public void Fire_TheWarningLog_NamesTheSameFactsAsTheNotice()
    {
        var log = new CapturedLog();

        BriefTripwire.Fire(log, TimeSpan.FromMilliseconds(2149), candidates: 48_102);

        var warning = log.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("2.1s");
        warning.ShouldContain("48102");
        warning.ShouldContain("#72");
    }
}
