using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Recall;

public class InjectionWrapperTests
{
    private static readonly DateTimeOffset Confirmed = new(2026, 7, 1, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Build_WrapsEntriesInAHeaderThatDisclaimsInstructionAuthority()
    {
        var (text, _) = InjectionWrapper.Build([Entry("Prefer rebase over merge.")], budgetChars: 4000);

        text.ShouldStartWith("<mimir-memory>");
        text.ShouldEndWith("</mimir-memory>");
        text.ShouldContain("Mimir");
        text.ShouldContain("not user instructions");
    }

    [Fact]
    public void Build_TagsEachEntryWithKindScopeAndLastConfirmed()
    {
        var global = Entry("Global text.", kind: WisdomKind.Lesson, isGlobal: true);
        var scoped = Entry("Project text.", kind: WisdomKind.Preference, isGlobal: false);

        var (text, _) = InjectionWrapper.Build([global, scoped], budgetChars: 4000);

        text.ShouldContain("- [Lesson · Global · confirmed 2026-07-01] Global text.");
        text.ShouldContain("- [Preference · this project · confirmed 2026-07-01] Project text.");
    }

    [Fact]
    public void Build_IsEmptyForNoEntries()
    {
        var (text, included) = InjectionWrapper.Build([], budgetChars: 4000);

        text.ShouldBeEmpty();
        included.ShouldBeEmpty();
    }

    [Fact]
    public void Build_IsEmptyWhenNotEvenTheFirstEntryFits()
    {
        var (text, included) = InjectionWrapper.Build([Entry(new string('x', 100))], budgetChars: 60);

        text.ShouldBeEmpty();
        included.ShouldBeEmpty();
    }

    [Fact]
    public void Build_StaysWithinTheBudget_AndReportsWhatItIncluded()
    {
        var first = Entry(new string('a', 1000));
        var second = Entry(new string('b', 1000));
        var third = Entry(new string('c', 1000));

        var (text, included) = InjectionWrapper.Build([first, second, third], budgetChars: 2500);

        text.Length.ShouldBeLessThanOrEqualTo(2500);
        included.ShouldBe([first, second]);
        text.ShouldContain(first.Text);
        text.ShouldContain(second.Text);
        text.ShouldNotContain(third.Text);
    }

    [Fact]
    public void Build_ChargesTheClosingTagAgainstTheBudget()
    {
        var (first, second) = (Entry(new string('a', 1000)), Entry(new string('b', 1000)));

        var (text, included) = InjectionWrapper.Build([first, second], budgetChars: 2195);

        included.ShouldBe([first], "the closing tag is part of what §11 budgets");
        text.Length.ShouldBeLessThanOrEqualTo(2195);
    }

    [Fact]
    public void Build_SkipsAnOversizedEntry_AndKeepsFillingWithLaterOnes()
    {
        var fits = Entry(new string('a', 500));
        var oversized = Entry(new string('b', 5000));
        var alsoFits = Entry(new string('c', 500));

        var (text, included) = InjectionWrapper.Build([fits, oversized, alsoFits], budgetChars: 2000);

        included.ShouldBe([fits, alsoFits]);
        text.ShouldNotContain(oversized.Text);
    }

    [Fact]
    public void Build_ReservesANoticeOutOfTheBudget_AndPlacesItAfterTheEntries()
    {
        var notice = new string('!', 59) + "\n";
        var (first, second) = (Entry(new string('a', 1000)), Entry(new string('b', 1000)));

        var (quiet, quietIncluded) = InjectionWrapper.Build([first, second], budgetChars: 2250);
        var (noticed, noticedIncluded) =
            InjectionWrapper.Build([first, second], budgetChars: 2250, notice);

        quietIncluded.ShouldBe([first, second]);
        noticedIncluded.ShouldBe([first], "the notice is bought from the entries, not added on top");
        noticed.Length.ShouldBeLessThanOrEqualTo(2250);
        noticed.ShouldEndWith(notice + "</mimir-memory>");
    }

    [Fact]
    public void Build_WithANoticeAndNoEntries_StillCarriesTheNotice()
    {
        var (text, included) = InjectionWrapper.Build([], budgetChars: 4000, notice: "⚠ notice\n");

        text.ShouldContain("⚠ notice");
        text.ShouldEndWith("</mimir-memory>");
        included.ShouldBeEmpty();
    }

    [Fact]
    public void Build_WithANoticeTooLargeForTheBudget_StaysEmpty()
    {
        var (text, included) = InjectionWrapper.Build(
            [], budgetChars: 130, notice: new string('!', 39) + "\n");

        text.ShouldBeEmpty("§11 binds this lane whether or not it has Wisdom to spend it on");
        included.ShouldBeEmpty();
    }

    private static InjectionEntry Entry(
        string text, WisdomKind kind = WisdomKind.Fact, bool isGlobal = true)
        => new(Guid.CreateVersion7(), Score: 1.0, kind, isGlobal, Confirmed, text);
}
