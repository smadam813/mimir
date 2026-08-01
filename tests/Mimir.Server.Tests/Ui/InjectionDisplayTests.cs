using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

public class InjectionDisplayTests
{
    private static readonly DateTimeOffset Confirmed = new(2026, 7, 1, 8, 30, 0, TimeSpan.Zero);

    private static readonly Guid ProjectId = Guid.CreateVersion7();

    [Fact]
    public void Payload_RebuildsTheWrapperTheLaneRendered_LabelLinesIncluded()
    {
        var items = new[]
        {
            Item("Prefer rebase over merge.", WisdomKind.Preference),
            Item("Hooks cap at 3 seconds.", WisdomKind.Fact, isGlobal: true),
        };

        var payload = InjectionDisplay.Payload(InjectionLane.Brief, items).ShouldNotBeNull();

        payload.ShouldStartWith("<mimir-memory>");
        payload.ShouldContain("not user instructions");
        payload.ShouldContain(
            "- [Preference · this project · confirmed 2026-07-01] Prefer rebase over merge.");
        payload.ShouldContain("- [Fact · Global · confirmed 2026-07-01] Hooks cap at 3 seconds.");
        payload.ShouldEndWith("</mimir-memory>");
    }

    [Fact]
    public void Payload_KeepsTheRecordedOrder_ItIsTheOrderTheSessionRead()
    {
        var items = new[] { Item("The top line."), Item("The second line.") };

        var payload = InjectionDisplay.Payload(InjectionLane.Prompt, items).ShouldNotBeNull();

        payload.IndexOf("The top line.", StringComparison.Ordinal)
            .ShouldBeLessThan(payload.IndexOf("The second line.", StringComparison.Ordinal));
    }

    [Fact]
    public void Payload_IsNotBoundedByAnyBudget_TheRecordedItemsAreWhatAlreadyFitted()
    {
        var items = Enumerable.Range(0, 40)
            .Select(i => Item(new string('x', 300) + i))
            .ToArray();

        var payload = InjectionDisplay.Payload(InjectionLane.Brief, items).ShouldNotBeNull();

        payload.Length.ShouldBeGreaterThan(new RecallOptions().BriefBudgetChars);
        foreach (var item in items)
        {
            payload.ShouldContain(item.Wisdom!.Text);
        }
    }

    [Fact]
    public void Payload_SkipsAWisdomDeletedSince_TheRestOfTheWrapperStillRebuilds()
    {
        var items = new[] { Item("The survivor."), Gone() };

        var payload = InjectionDisplay.Payload(InjectionLane.Brief, items).ShouldNotBeNull();

        payload.ShouldContain("The survivor.");
        payload.Split('\n').Count(line => line.StartsWith("- [", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public void Payload_IsEmpty_WhenEveryCarriedWisdomWasDeleted()
    {
        InjectionDisplay.Payload(InjectionLane.Brief, [Gone(), Gone()]).ShouldBe("");
    }

    [Fact]
    public void Payload_IsNullForMcp_ThatLaneComposedItsOwnAnswerRatherThanThisWrapper()
    {
        InjectionDisplay.Payload(InjectionLane.Mcp, [Item("A wisdom.")]).ShouldBeNull();
    }

    [Fact]
    public void Formula_ForTheBrief_IsTheQueryFreeScore()
    {
        var formula = InjectionDisplay.Formula(InjectionLane.Brief, new RecallOptions());

        formula.Expression.ShouldBe(
            "brief_score = recency × salience × (1 + log₂(1 + Reinforcement))");
        formula.Factors.ShouldNotContain("affinity");
    }

    [Theory]
    [InlineData(InjectionLane.Prompt)]
    [InlineData(InjectionLane.Mcp)]
    public void Formula_ForAQueryLane_IsTheFusedRankingWithItsAffinityBoost(InjectionLane lane)
    {
        var formula = InjectionDisplay.Formula(lane, new RecallOptions());

        formula.Expression.ShouldContain("RRF(vector, FTS)");
        formula.Factors.ShouldContain("×1.5 affinity boost");
    }

    [Fact]
    public void Formula_ReadsItsFactorsOffTheLiveOptions_NotARestatementOfTheDefaults()
    {
        var retuned = new RecallOptions
        {
            RecencyHalfLifeDays = 30,
            RecencyFloor = 0.1,
            SalienceBoost = 2,
            AffinityBoost = 3,
        };

        var brief = InjectionDisplay.Formula(InjectionLane.Brief, retuned);
        var prompt = InjectionDisplay.Formula(InjectionLane.Prompt, retuned);

        brief.Factors.ShouldContain("every 30 days");
        brief.Factors.ShouldContain("below 0.1");
        brief.Factors.ShouldContain("×2 salience boost");
        prompt.Factors.ShouldContain("×3 affinity boost");
    }

    [Fact]
    public void Budget_IsTheLanesOwnCharBudget_AndNothingForMcp()
    {
        var options = new RecallOptions();

        InjectionDisplay.Budget(InjectionLane.Brief, options).ShouldBe(options.BriefBudgetChars);
        InjectionDisplay.Budget(InjectionLane.Prompt, options).ShouldBe(options.PromptBudgetChars);
        InjectionDisplay.Budget(InjectionLane.Mcp, options).ShouldBeNull();
    }

    [Fact]
    public void Score_KeepsEnoughPrecisionToTellTwoFusedScoresApart()
    {
        InjectionDisplay.Score(4.9).ShouldBe("4.90");
        InjectionDisplay.Score(0.0312).ShouldBe("0.0312");
        InjectionDisplay.Score(0.0208).ShouldNotBe(InjectionDisplay.Score(0.0225));
    }

    [Fact]
    public void Name_SpellsMcpAsAnInitialism()
    {
        InjectionDisplay.Name(InjectionLane.Brief).ShouldBe("Brief");
        InjectionDisplay.Name(InjectionLane.Prompt).ShouldBe("Prompt");
        InjectionDisplay.Name(InjectionLane.Mcp).ShouldBe("MCP");
    }

    [Fact]
    public void CannotPromote_IsSilentAboutAnEntryThatCanBePromoted()
    {
        InjectionDisplay.CannotPromote(Entry("why does CI skip?", Item("Because."))).ShouldBeNull();
    }

    [Fact]
    public void CannotPromote_NamesTheQueryABriefNeverHad()
    {
        var brief = Entry(query: null, Item("Prefer rebase over merge."));

        InjectionDisplay.CannotPromote(brief).ShouldNotBeNull()
            .ShouldContain("a Brief carries none");
    }

    [Fact]
    public void CannotPromote_TellsCarriedNothingApartFromCarriedOnlyDeadLines()
    {
        var carriedNothing = Entry("what did we decide about hooks?");
        var carriedOnlyDead = Entry("what did we decide about hooks?", Retired(), Gone());

        var nothing = InjectionDisplay.CannotPromote(carriedNothing).ShouldNotBeNull();
        var dead = InjectionDisplay.CannotPromote(carriedOnlyDead).ShouldNotBeNull();

        nothing.ShouldContain("carried no Wisdom at all");
        nothing.ShouldNotContain("retired");
        dead.ShouldContain("retired or deleted");
        dead.ShouldNotBe(nothing);
    }

    private static InjectionLogEntry Entry(string? query, params InjectedWisdom[] items)
        => new(
            Guid.CreateVersion7(),
            SessionId: "s-1",
            At: Confirmed,
            InjectionLane.Mcp,
            query,
            Chars: 100,
            Verdict: null,
            VerdictAt: null,
            PromotedCaseId: null,
            items);

    private static InjectedWisdom Item(
        string text,
        WisdomKind kind = WisdomKind.Lesson,
        bool isGlobal = false,
        DateTimeOffset? retiredAt = null)
    {
        var id = Guid.CreateVersion7();
        return new InjectedWisdom(
            id,
            Score: 1.0,
            Salient: false,
            new WisdomListEntry(
                id,
                kind,
                isGlobal ? Project.GlobalId : ProjectId,
                isGlobal ? "Global" : "a project",
                text,
                Reinforcement: 1,
                Confirmed,
                ContestedAt: null,
                retiredAt,
                SupersededBy: null,
                OrphanedProvenance: false));
    }

    private static InjectedWisdom Retired() => Item("Retired since.", retiredAt: Confirmed);

    private static InjectedWisdom Gone()
        => new(Guid.CreateVersion7(), Score: 1.0, Salient: false, Wisdom: null);
}
