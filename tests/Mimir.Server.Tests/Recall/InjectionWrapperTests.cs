using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The §7 provenance-labeled wrapper shared by the ambient lanes: a header identifying the content
/// as Mimir memory (not user instructions), each Wisdom tagged kind/scope/last-confirmed, filled to
/// the caller's char budget in the caller's order.
///
/// Deliberately Postgres-free — the budget arithmetic is wrong or right without a database in the
/// picture, and these pins have to run on the machine where it is being changed rather than only
/// where the harness can reach one.
/// </summary>
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
        // The header and one labeled 1000-char entry come to 1145 chars, a second takes it to 2187,
        // and the footer to 2202. At a budget of 2195 the second entry fits only if the closing tag
        // is measured as free — which is how a Brief overruns §11 by exactly the footer.
        var (first, second) = (Entry(new string('a', 1000)), Entry(new string('b', 1000)));

        var (text, included) = InjectionWrapper.Build([first, second], budgetChars: 2195);

        included.ShouldBe([first], "the closing tag is part of what §11 budgets");
        text.Length.ShouldBeLessThanOrEqualTo(2195);
    }

    [Fact]
    public void Build_SkipsAnOversizedEntry_AndKeepsFillingWithLaterOnes()
    {
        // §7 "filled to ≤ 4,000 chars": one oversized entry must not starve the rest of the Brief.
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
        // The header, two 1000-char entries and the footer come to 2202 chars: inside a 2250-char
        // budget on their own, and one entry too many once a 60-char notice is reserved as well.
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

        // Injecting nothing is how a lane says "nothing to recall". A notice that vanished with
        // the last entry would therefore be silent in exactly the case it was raised for.
        text.ShouldContain("⚠ notice");
        text.ShouldEndWith("</mimir-memory>");
        included.ShouldBeEmpty();
    }

    [Fact]
    public void Build_WithANoticeTooLargeForTheBudget_StaysEmpty()
    {
        // The budget has to straddle the notice, or this pins nothing: header and footer come to
        // 118 chars, which fits inside 130 on its own, and the 40-char notice is what pushes it
        // out. Pick a budget the wrapper alone already overruns and the result is empty whether or
        // not the notice was ever reserved.
        var (text, included) = InjectionWrapper.Build(
            [], budgetChars: 130, notice: new string('!', 39) + "\n");

        text.ShouldBeEmpty("§11 binds this lane whether or not it has Wisdom to spend it on");
        included.ShouldBeEmpty();
    }

    private static InjectionEntry Entry(
        string text, WisdomKind kind = WisdomKind.Fact, bool isGlobal = true)
        => new(Guid.CreateVersion7(), Score: 1.0, kind, isGlobal, Confirmed, text);
}
