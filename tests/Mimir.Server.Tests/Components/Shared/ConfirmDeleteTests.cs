using Bunit;
using Mimir.Server.Components.Shared;

namespace Mimir.Server.Tests.Components.Shared;

/// <summary>
/// #130's disconnected-tier flagship: the §8.2 confirmation as a host actually drives it — click
/// to arm, a parameter change to move the subject, click to confirm. <see cref="ConfirmArmingTests"/>
/// pins what the latch decides; this pins that the markup asks it. That wiring is the half #106
/// broke and no pure test could reach: the latch was right the whole time, and the bug was a host
/// that never told it the subject had moved.
/// <para>
/// No database and no DI, so these run on a Docker-less machine — the tier's whole point.
/// </para>
/// </summary>
public class ConfirmDeleteTests : RenderTestBase
{
    private static readonly Guid RecordA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RecordB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>What the host was told to delete, in the order it was told — empty until confirmed.</summary>
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

    /// <summary>
    /// The whole of #106, along the path it actually took: the host keeps this component mounted
    /// and repoints it at the record the curator just selected. If the markup stopped binding the
    /// subject on every parameter set, the incoming record would arrive with "Delete forever" one
    /// click away, under a prompt written about the outgoing one.
    /// </summary>
    [Fact]
    public void MovingToAnotherRecord_ReturnsToResting()
    {
        var confirm = RenderAt(RecordA);
        confirm.Find("button").Click();

        confirm.Render(p => p.Add(c => c.SubjectKey, RecordB));

        confirm.FindAll("[role=alertdialog]").ShouldBeEmpty();
        confirm.Find("button").TextContent.Trim().ShouldBe("Delete");
    }

    /// <summary>
    /// The other half: a re-render the host does for its own reasons — a list refresh, a feed
    /// announcement — must not take the consequence away while the curator is reading it.
    /// </summary>
    [Fact]
    public void ReRenderingTheSameRecord_KeepsTheConsequenceUp()
    {
        var confirm = RenderAt(RecordA);
        confirm.Find("button").Click();

        confirm.Render(p => p.Add(c => c.Label, "Delete Wisdom"));

        confirm.Find("[role=alertdialog]").ShouldNotBeNull();
    }

    /// <summary>
    /// Confirming carries the id the prompt was written about (#106's second half), rather than
    /// leaving the host to re-read a selection that has already moved on.
    /// </summary>
    [Fact]
    public void Confirming_CarriesTheSubjectItNamed()
    {
        var confirm = RenderAt(RecordA);
        confirm.Find("button").Click();

        confirm.Find("button.danger-fill").Click();

        _deleted.ShouldBe([RecordA]);
    }

    /// <summary>Cancelling is the reversal arming promises, and deletes nothing.</summary>
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
