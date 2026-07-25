using Microsoft.EntityFrameworkCore;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The one keeper of the §7 recording rules, exercised directly rather than through a lane: the
/// provenance-labeled wrapper the ambient lanes share, the budget it fills, and the empty-trace
/// rule in both of its shapes — read off what was included for a rendered injection, off the
/// answer for one <c>mimir_search</c> composed itself.
/// </summary>
public sealed class InjectionLogTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Confirmed = new(2026, 7, 1, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Render_WrapsEntriesInAHeaderThatDisclaimsInstructionAuthority()
    {
        var text = await RenderAndRecordAsync([Entry("Prefer rebase over merge.")]);

        text.ShouldStartWith("<mimir-memory>");
        text.ShouldEndWith("</mimir-memory>");
        text.ShouldContain("Mimir");
        text.ShouldContain("not user instructions");
    }

    [Fact]
    public async Task Render_TagsEachEntryWithKindScopeAndLastConfirmed()
    {
        var global = Entry("Global text.", kind: WisdomKind.Lesson, isGlobal: true);
        var scoped = Entry("Project text.", kind: WisdomKind.Preference, isGlobal: false);

        var text = await RenderAndRecordAsync([global, scoped]);

        text.ShouldContain("- [Lesson · Global · confirmed 2026-07-01] Global text.");
        text.ShouldContain("- [Preference · this project · confirmed 2026-07-01] Project text.");
    }

    [Fact]
    public async Task Render_StaysWithinTheBudget_AndLogsOnlyWhatMadeItIn()
    {
        var first = Entry(new string('a', 1000));
        var second = Entry(new string('b', 1000));
        var third = Entry(new string('c', 1000));

        var text = await RenderAndRecordAsync([first, second, third], budgetChars: 2500);

        text.Length.ShouldBeLessThanOrEqualTo(2500);
        text.ShouldContain(first.Text);
        text.ShouldContain(second.Text);
        text.ShouldNotContain(third.Text);
        var logged = await SingleInjectionAsync();
        logged.Items.Select(i => i.WisdomId).ShouldBe([first.WisdomId, second.WisdomId]);
        logged.Chars.ShouldBe(text.Length);
    }

    [Fact]
    public async Task Render_SkipsAnOversizedEntry_AndKeepsFillingWithLaterOnes()
    {
        // §7 "filled to ≤ 4,000 chars": one oversized entry must not starve the rest of the Brief.
        var fits = Entry(new string('a', 500));
        var oversized = Entry(new string('b', 5000));
        var alsoFits = Entry(new string('c', 500));

        var text = await RenderAndRecordAsync([fits, oversized, alsoFits], budgetChars: 2000);

        text.ShouldNotContain(oversized.Text);
        (await SingleInjectionAsync()).Items.Select(i => i.WisdomId)
            .ShouldBe([fits.WisdomId, alsoFits.WisdomId]);
    }

    [Fact]
    public async Task Render_ReservesANoticeOutOfTheBudget_AndPlacesItAfterTheEntries()
    {
        // The header, two 1000-char entries and the footer come to 2202 chars: inside a 2250-char
        // budget on their own, and one entry too many once a 60-char notice is reserved as well.
        var notice = new string('!', 59) + "\n";
        var (first, second) = (Entry(new string('a', 1000)), Entry(new string('b', 1000)));
        var (quietSession, noticedSession) = (NewSessionId(), NewSessionId());

        var quiet = await RenderAndRecordAsync(
            [first, second], budgetChars: 2250, sessionId: quietSession);
        var noticed = await RenderAndRecordAsync(
            [first, second], budgetChars: 2250, notice: notice, sessionId: noticedSession);

        (await InjectionForAsync(quietSession)).Items.Select(i => i.WisdomId)
            .ShouldBe([first.WisdomId, second.WisdomId]);
        (await InjectionForAsync(noticedSession)).Items.Select(i => i.WisdomId)
            .ShouldBe([first.WisdomId], "the notice is bought from the entries, not added on top");
        quiet.ShouldContain(second.Text);
        noticed.ShouldNotContain(second.Text);
        noticed.Length.ShouldBeLessThanOrEqualTo(2250);
        noticed.ShouldEndWith(notice + "</mimir-memory>");
    }

    [Fact]
    public async Task Render_WithANoticeAndNoEntries_StillCarriesTheNotice_ButLogsNothing()
    {
        var text = await RenderAndRecordAsync([], notice: "⚠ notice\n");

        // Injecting nothing is how a lane says "nothing to recall". A notice that vanished with
        // the last entry would therefore be silent in exactly the case it was raised for — but a
        // notice is not an injection, so §7 still leaves no trace of it.
        text.ShouldContain("⚠ notice");
        text.ShouldEndWith("</mimir-memory>");
        await ShouldHaveNoInjectionAsync("a notice with no Wisdom behind it is not an injection (§7)");
    }

    [Fact]
    public async Task Render_WithANoticeTooLargeForTheBudget_StaysEmpty()
    {
        var text = await RenderAndRecordAsync(
            [], budgetChars: 60, notice: new string('!', 500) + "\n");

        text.ShouldBeEmpty("§11 binds this lane whether or not it has Wisdom to spend it on");
        await ShouldHaveNoInjectionAsync();
    }

    [Fact]
    public async Task RenderAndRecord_WithNothingIncludable_ReturnsEmpty_AndLogsNothing()
    {
        var text = await RenderAndRecordAsync([], budgetChars: 4000);
        var overflowed = await RenderAndRecordAsync([Entry(new string('x', 100))], budgetChars: 60);

        text.ShouldBeEmpty();
        overflowed.ShouldBeEmpty();
        await ShouldHaveNoInjectionAsync("empty decisions leave no trace (§7)");
    }

    [Fact]
    public async Task RenderAndRecord_LogsOneRowCarryingTheLanesContext()
    {
        var project = await AddProjectAsync("keeper");
        var entry = Entry("something worth injecting");
        var sessionId = NewSessionId();

        var text = await Log().RenderAndRecordAsync(
            new InjectionContext(
                InjectionLane.Prompt, sessionId, project.Id, "why does this build fail?"),
            [entry],
            budgetChars: 4000,
            notice: null,
            Token);

        var logged = await SingleInjectionAsync();
        logged.SessionId.ShouldBe(sessionId);
        logged.ProjectId.ShouldBe(project.Id);
        logged.Lane.ShouldBe(InjectionLane.Prompt);
        logged.QueryContext.ShouldBe("why does this build fail?");
        logged.At.ShouldBe(Now, customMessage: "the keeper owns the clock the row is stamped from");
        logged.Chars.ShouldBe(text.Length);
        logged.Items.Select(i => i.WisdomId).ShouldBe([entry.WisdomId]);
        logged.Items[0].Score.ShouldBe(entry.Score);
    }

    [Fact]
    public async Task Record_WithAnEmptyAnswer_LogsNothing()
    {
        var project = await AddProjectAsync("keeper");

        await Log().RecordAsync(
            new InjectionContext(
                InjectionLane.Mcp, "mcp-session", project.Id, "a query nothing matched"),
            text: "",
            [Entry("never rendered")],
            Token);

        await ShouldHaveNoInjectionAsync("an empty answer leaves no Injection row (§7)");
    }

    [Fact]
    public async Task Record_WithAnAnswerCarryingNoWisdom_StillLogsARow()
    {
        var project = await AddProjectAsync("keeper");

        await Log().RecordAsync(
            new InjectionContext(
                InjectionLane.Mcp, "mcp-session", project.Id, "deploy the pipeline"),
            text: "Mimir results: one Episode, no Wisdom.",
            [],
            Token);

        // mimir_search may answer with Episodes alone. That answer went in front of the session,
        // so it is an injection — one with no items, not one that never happened.
        var logged = await SingleInjectionAsync();
        logged.Lane.ShouldBe(InjectionLane.Mcp);
        logged.Chars.ShouldBe("Mimir results: one Episode, no Wisdom.".Length);
        logged.Items.ShouldBeEmpty();
    }

    private InjectionLog Log() => new(Context, Clock);

    private async Task<string> RenderAndRecordAsync(
        IReadOnlyList<InjectionEntry> entries,
        int budgetChars = 4000,
        string? notice = null,
        string? sessionId = null)
    {
        var project = await AddProjectAsync("keeper");
        return await Log().RenderAndRecordAsync(
            new InjectionContext(
                InjectionLane.Brief, sessionId ?? NewSessionId(), project.Id, QueryContext: null),
            entries,
            budgetChars,
            notice,
            Token);
    }

    private async Task<Injection> SingleInjectionAsync()
        => await FromDb(db => db.Injections.SingleAsync(Token));

    private async Task<Injection> InjectionForAsync(string sessionId)
        => await FromDb(db => db.Injections.SingleAsync(i => i.SessionId == sessionId, Token));

    private async Task ShouldHaveNoInjectionAsync(string? because = null)
        => (await FromDb(db => db.Injections.CountAsync(Token))).ShouldBe(0, because);

    private static string NewSessionId() => $"sess-{Guid.NewGuid():N}";

    private static InjectionEntry Entry(
        string text, WisdomKind kind = WisdomKind.Fact, bool isGlobal = true)
        => new(Guid.CreateVersion7(), Score: 1.0, kind, isGlobal, Confirmed, text);
}
