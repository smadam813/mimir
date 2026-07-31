using Mimir.Server.Components.Shared;

namespace Mimir.Server.Tests.Components.Shared;

/// <summary>
/// #106: the §8.2 confirmation disarms itself when the record it is armed against changes. Nothing
/// renders a component in a test here, so this is the seam that rule is pinnable at — the latch
/// <c>ConfirmDelete</c> delegates its whole state to. No SQL and no DI, so it runs everywhere,
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
