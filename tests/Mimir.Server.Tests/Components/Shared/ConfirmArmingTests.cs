using Mimir.Server.Components.Shared;

namespace Mimir.Server.Tests.Components.Shared;

/// <summary>
/// #106: the §8.2 confirmation disarms itself when the record it is armed against changes. This is
/// the latch <c>ConfirmDelete</c> delegates its whole state to, and what it *decides* stays here
/// rather than in an <c>@code</c> block even now that bUnit renders components — the placement
/// ladder in <c>.claude/rules/blazor-ui.md</c>. <c>ConfirmDeleteTests</c> is the other half: that
/// the markup asks this latch, one directory over. No SQL and no DI, so this runs everywhere,
/// including with no Postgres reachable.
/// </summary>
public class ConfirmArmingTests
{
    private static readonly Guid RecordA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RecordB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Starts_Disarmed()
    {
        var arming = new ConfirmArming();

        arming.Bind(RecordA);

        arming.Armed.ShouldBeFalse();
    }

    [Fact]
    public void Arming_ShowsTheConsequence()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);

        arming.Arm();

        arming.Armed.ShouldBeTrue();
    }

    /// <summary>
    /// The whole of #106: the Episode drill-down stays mounted across a selection, so an armed
    /// Delete would otherwise paint the incoming record with "Delete forever" one click away.
    /// </summary>
    [Fact]
    public void BindingAnotherRecord_Disarms()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.Bind(RecordB);

        arming.Armed.ShouldBeFalse();
    }

    /// <summary>
    /// The other half of that rule, and the one a disarm-on-every-parameter-set would break: a host
    /// re-renders for reasons of its own — a list refresh, a feed announcement — while the curator
    /// is still reading the consequence, and taking it away under them is its own bug.
    /// </summary>
    [Fact]
    public void RebindingTheSameRecord_StaysArmed()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.Bind(RecordA);

        arming.Armed.ShouldBeTrue();
    }

    /// <summary>
    /// The stale-click guard. Blazor Server keeps a disposed handler's binding alive until the
    /// client acknowledges the render batch that dropped it, so a "Delete forever" queued behind a
    /// selection change is delivered after the latch has disarmed and repointed. Answering true
    /// there would hard-delete a record whose prompt was never shown.
    /// </summary>
    [Fact]
    public void ConfirmingWhatIsNotArmed_IsRefused()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);

        arming.TryConfirm().ShouldBeFalse();
    }

    /// <summary>
    /// The same refusal along the path that produces it: armed against A, the selection moves to B,
    /// and the click that was already in flight lands.
    /// </summary>
    [Fact]
    public void ConfirmingAfterTheSubjectMoved_IsRefused()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.Bind(RecordB);

        arming.TryConfirm().ShouldBeFalse();
    }

    /// <summary>
    /// And the one-shot: a double-dispatched click confirms once, not twice.
    /// </summary>
    [Fact]
    public void ConfirmingWhatIsArmed_SucceedsExactlyOnce()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.TryConfirm().ShouldBeTrue();

        arming.Armed.ShouldBeFalse();
        arming.TryConfirm().ShouldBeFalse();
    }

    /// <summary>
    /// Confirming is a one-shot: the host re-reads and the same component may well be pointed at the
    /// same record again, and it must come back resting rather than still armed.
    /// </summary>
    [Fact]
    public void Disarming_ReturnsToResting()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.Disarm();

        arming.Armed.ShouldBeFalse();
    }
}
