using Mimir.Server.Configuration;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The §8.3 surface's pure presentation: the payload an ambient lane put in front of a session,
/// rebuilt from what the entry recorded, and the §7 formula the screen states beside the scores.
///
/// Deliberately Postgres-free. Both are wrong or right with no database in the picture, and the
/// machine most likely to break them is the one with no Docker running — where a Postgres-backed
/// pin would skip rather than fail.
/// </summary>
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
        // Well past the Brief's own 4,000-char §11 budget: these lines were recorded, so they were
        // injected, and a rebuild that re-measured them against the budget would drop lines the
        // session demonstrably read.
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
        // mimir_search is capped by result count, not chars — quoting one would invent it.
        InjectionDisplay.Budget(InjectionLane.Mcp, options).ShouldBeNull();
    }

    [Fact]
    public void Score_KeepsEnoughPrecisionToTellTwoFusedScoresApart()
    {
        // A Brief's scale runs to single digits; a query lane's fused score sits near a hundredth,
        // where two decimals would round every row in an entry to the same figure.
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

    private static InjectedWisdom Item(
        string text, WisdomKind kind = WisdomKind.Lesson, bool isGlobal = false)
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
                RetiredAt: null,
                SupersededBy: null,
                OrphanedProvenance: false));
    }

    /// <summary>An item whose Wisdom was hard-deleted after the injection (§10).</summary>
    private static InjectedWisdom Gone()
        => new(Guid.CreateVersion7(), Score: 1.0, Salient: false, Wisdom: null);
}
