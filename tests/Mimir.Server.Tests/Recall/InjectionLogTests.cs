using Microsoft.EntityFrameworkCore;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Recall;

public sealed class InjectionLogTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Confirmed = new(2026, 7, 1, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task RenderAndRecord_LogsOnlyWhatTheWrapperIncluded()
    {
        var first = Entry(new string('a', 1000));
        var second = Entry(new string('b', 1000));
        var third = Entry(new string('c', 1000));

        var text = await RenderAndRecordAsync([first, second, third], budgetChars: 2500);

        var logged = await SingleInjectionAsync();
        logged.Items.Select(i => i.WisdomId).ShouldBe([first.WisdomId, second.WisdomId]);
        logged.Chars.ShouldBe(text.Length);
    }

    [Fact]
    public async Task RenderAndRecord_WithANoticeAndNoEntries_ReturnsIt_ButLogsNothing()
    {
        var text = await RenderAndRecordAsync([], notice: "⚠ notice\n");

        text.ShouldContain("⚠ notice");
        await ShouldHaveNoInjectionAsync("a notice with no Wisdom behind it is not an injection (§7)");
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

        var logged = await SingleInjectionAsync();
        logged.Lane.ShouldBe(InjectionLane.Mcp);
        logged.Chars.ShouldBe("Mimir results: one Episode, no Wisdom.".Length);
        logged.Items.ShouldBeEmpty();
    }

    private InjectionLog Log() => new(Context, Clock);

    private async Task<string> RenderAndRecordAsync(
        IReadOnlyList<InjectionEntry> entries, int budgetChars = 4000, string? notice = null)
    {
        var project = await AddProjectAsync("keeper");
        return await Log().RenderAndRecordAsync(
            new InjectionContext(
                InjectionLane.Brief, NewSessionId(), project.Id, QueryContext: null),
            entries,
            budgetChars,
            notice,
            Token);
    }

    private async Task<Injection> SingleInjectionAsync()
        => await FromDb(db => db.Injections.SingleAsync(Token));

    private async Task ShouldHaveNoInjectionAsync(string? because = null)
        => (await FromDb(db => db.Injections.CountAsync(Token))).ShouldBe(0, because);

    private static string NewSessionId() => $"sess-{Guid.NewGuid():N}";

    private static InjectionEntry Entry(
        string text, WisdomKind kind = WisdomKind.Fact, bool isGlobal = true)
        => new(Guid.CreateVersion7(), Score: 1.0, kind, isGlobal, Confirmed, text);
}
