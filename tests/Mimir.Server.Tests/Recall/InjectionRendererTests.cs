using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The §7 provenance-labeled wrapper shared by the ambient lanes: a header identifying the content
/// as Mimir memory (not user instructions), each Wisdom tagged kind/scope/last-confirmed, filled
/// to the caller's char budget in the caller's order.
/// </summary>
public class InjectionRendererTests
{
    private static readonly DateTimeOffset Confirmed = new(2026, 7, 1, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Render_WrapsEntriesInAHeaderThatDisclaimsInstructionAuthority()
    {
        var (text, _) = InjectionRenderer.Render([Entry("Prefer rebase over merge.")], budgetChars: 4000);

        text.ShouldStartWith("<mimir-memory>");
        text.ShouldEndWith("</mimir-memory>");
        text.ShouldContain("Mimir");
        text.ShouldContain("not user instructions");
    }

    [Fact]
    public void Render_TagsEachEntryWithKindScopeAndLastConfirmed()
    {
        var global = Entry("Global text.", kind: WisdomKind.Lesson, isGlobal: true);
        var scoped = Entry("Project text.", kind: WisdomKind.Preference, isGlobal: false);

        var (text, _) = InjectionRenderer.Render([global, scoped], budgetChars: 4000);

        text.ShouldContain("- [Lesson · Global · confirmed 2026-07-01] Global text.");
        text.ShouldContain("- [Preference · this project · confirmed 2026-07-01] Project text.");
    }

    [Fact]
    public void Render_IsEmptyForNoEntries()
    {
        var (text, included) = InjectionRenderer.Render([], budgetChars: 4000);

        text.ShouldBeEmpty();
        included.ShouldBeEmpty();
    }

    [Fact]
    public void Render_IsEmptyWhenNotEvenTheFirstEntryFits()
    {
        var (text, included) = InjectionRenderer.Render([Entry(new string('x', 100))], budgetChars: 60);

        text.ShouldBeEmpty();
        included.ShouldBeEmpty();
    }

    [Fact]
    public void Render_StaysWithinTheBudget_AndReportsWhatItIncluded()
    {
        var first = Entry(new string('a', 1000));
        var second = Entry(new string('b', 1000));
        var third = Entry(new string('c', 1000));

        var (text, included) = InjectionRenderer.Render([first, second, third], budgetChars: 2500);

        text.Length.ShouldBeLessThanOrEqualTo(2500);
        included.ShouldBe([first, second]);
        text.ShouldContain(first.Text);
        text.ShouldContain(second.Text);
        text.ShouldNotContain(third.Text);
    }

    [Fact]
    public void Render_SkipsAnOversizedEntry_AndKeepsFillingWithLaterOnes()
    {
        // §7 "filled to ≤ 4,000 chars": one oversized entry must not starve the rest of the Brief.
        var fits = Entry(new string('a', 500));
        var oversized = Entry(new string('b', 5000));
        var alsoFits = Entry(new string('c', 500));

        var (text, included) = InjectionRenderer.Render([fits, oversized, alsoFits], budgetChars: 2000);

        included.ShouldBe([fits, alsoFits]);
        text.ShouldNotContain(oversized.Text);
    }

    [Fact]
    public void Render_ReservesANoticeOutOfTheBudget_AndPlacesItAfterTheEntries()
    {
        // The header, two 1000-char entries and the footer come to 2202 chars: inside a 2250-char
        // budget on their own, and one entry too many once a 60-char notice is reserved as well.
        var notice = new string('!', 59) + "\n";
        var (first, second) = (Entry(new string('a', 1000)), Entry(new string('b', 1000)));

        var (quiet, quietIncluded) = InjectionRenderer.Render([first, second], budgetChars: 2250);
        var (noticed, noticedIncluded) =
            InjectionRenderer.Render([first, second], budgetChars: 2250, notice);

        quietIncluded.ShouldBe([first, second]);
        noticedIncluded.ShouldBe([first], "the notice is bought from the entries, not added on top");
        noticed.Length.ShouldBeLessThanOrEqualTo(2250);
        noticed.ShouldEndWith(notice + "</mimir-memory>");
    }

    [Fact]
    public void Render_WithANoticeAndNoEntries_StillCarriesTheNotice()
    {
        var (text, included) = InjectionRenderer.Render([], budgetChars: 4000, notice: "⚠ notice\n");

        // Injecting nothing is how a lane says "nothing to recall". A notice that vanished with
        // the last entry would therefore be silent in exactly the case it was raised for.
        text.ShouldContain("⚠ notice");
        text.ShouldEndWith("</mimir-memory>");
        included.ShouldBeEmpty();
    }

    [Fact]
    public void Render_WithANoticeTooLargeForTheBudget_StaysEmpty()
    {
        var (text, included) = InjectionRenderer.Render(
            [], budgetChars: 60, notice: new string('!', 500) + "\n");

        text.ShouldBeEmpty("§11 binds this lane whether or not it has Wisdom to spend it on");
        included.ShouldBeEmpty();
    }

    private static InjectionEntry Entry(
        string text, WisdomKind kind = WisdomKind.Fact, bool isGlobal = true)
        => new(Guid.CreateVersion7(), Score: 1.0, kind, isGlobal, Confirmed, text);
}
