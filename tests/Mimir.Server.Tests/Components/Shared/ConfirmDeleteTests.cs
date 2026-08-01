using Bunit;
using Mimir.Server.Components.Shared;

namespace Mimir.Server.Tests.Components.Shared;

public class ConfirmDeleteTests : RenderTestBase
{
    private static readonly Guid RecordA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RecordB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly List<Guid> _deleted = [];

    private IRenderedComponent<ConfirmDelete> RenderAt(Guid subject)
        => Render<ConfirmDelete>(p => p
            .Add(c => c.Label, "Delete")
            .Add(c => c.Prompt, "Delete this Wisdom forever?")
            .Add(c => c.SubjectKey, subject)
            .Add(c => c.OnConfirm, _deleted.Add));

    [Fact]
    public void Resting_OffersOneButtonAndNoConsequence()
    {
        var confirm = RenderAt(RecordA);

        confirm.Find("button").TextContent.Trim().ShouldBe("Delete");
        confirm.FindAll("[role=alertdialog]").ShouldBeEmpty();
    }

    [Fact]
    public void Arming_SwapsInTheConsequenceAndAnExplicitChoice()
    {
        var confirm = RenderAt(RecordA);

        confirm.Find("button").Click();

        confirm.Find("[role=alertdialog]").TextContent.ShouldContain("Delete this Wisdom forever?");
        confirm.FindAll("button").Select(b => b.TextContent.Trim())
            .ShouldBe(["Delete forever", "Cancel"]);
    }

    [Fact]
    public void MovingToAnotherRecord_ReturnsToResting()
    {
        var confirm = RenderAt(RecordA);
        confirm.Find("button").Click();

        confirm.Render(p => p.Add(c => c.SubjectKey, RecordB));

        confirm.FindAll("[role=alertdialog]").ShouldBeEmpty();
        confirm.Find("button").TextContent.Trim().ShouldBe("Delete");
    }

    [Fact]
    public void ReRenderingTheSameRecord_KeepsTheConsequenceUp()
    {
        var confirm = RenderAt(RecordA);
        confirm.Find("button").Click();

        confirm.Render(p => p.Add(c => c.Label, "Delete Wisdom"));

        confirm.Find("[role=alertdialog]").ShouldNotBeNull();
    }

    [Fact]
    public void Confirming_CarriesTheSubjectItNamed()
    {
        var confirm = RenderAt(RecordA);
        confirm.Find("button").Click();

        confirm.Find("button.danger-fill").Click();

        _deleted.ShouldBe([RecordA]);
    }

    [Fact]
    public void Cancelling_ReturnsToRestingWithoutDeleting()
    {
        var confirm = RenderAt(RecordA);
        confirm.Find("button").Click();

        confirm.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        confirm.FindAll("[role=alertdialog]").ShouldBeEmpty();
        _deleted.ShouldBeEmpty();
    }
}
