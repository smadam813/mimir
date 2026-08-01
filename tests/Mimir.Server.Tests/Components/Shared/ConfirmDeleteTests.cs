using Bunit;
using Mimir.Server.Components.Shared;

namespace Mimir.Server.Tests.Components.Shared;

public class ConfirmDeleteTests : RenderTestBase
{
    private static readonly Guid RecordA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RecordB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly List<Guid> _deleted = [];

    private IRenderedComponent<ConfirmDelete> RenderAt(Guid subject, bool subtle = false)
        => Render<ConfirmDelete>(p => p
            .Add(c => c.Label, "Delete")
            .Add(c => c.Prompt, "Delete this Wisdom forever?")
            .Add(c => c.SubjectKey, subject)
            .Add(c => c.Subtle, subtle)
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

    /// <summary>
    /// <c>Subtle</c> mutes the *resting* button only. Arming is reversible, so the resting button
    /// is not itself the destructive act — and the Event stream draws one of these per Event, where
    /// a row of danger-hued buttons would out-shout the salience mark that stream exists to show
    /// (#95). Both states are asserted from one render, because the rule is the contrast between
    /// them: muting the armed state too would be the regression nobody sees until a delete lands.
    /// </summary>
    [Fact]
    public void Subtle_MutesTheRestingButtonAndLeavesTheConsequenceRed()
    {
        var confirm = RenderAt(RecordA, subtle: true);

        confirm.Find("button").ClassList.ShouldContain("is-subtle");
        confirm.Find("button").ClassList.ShouldNotContain("danger-outline");

        confirm.Find("button").Click();

        confirm.Find("[role=alertdialog] button.danger-fill").ShouldNotBeNull();
    }

    /// <summary>
    /// The default is the loud one: a Delete offered on its own — the Wisdom detail's, the whole
    /// Episode's — is not competing with a stream of siblings and reads as what it is.
    /// </summary>
    [Fact]
    public void WithoutSubtle_TheRestingButtonWearsTheDangerOutline()
        => RenderAt(RecordA).Find("button").ClassList.ShouldContain("danger-outline");
}
