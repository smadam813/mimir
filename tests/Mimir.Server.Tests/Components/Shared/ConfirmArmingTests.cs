using Mimir.Server.Components.Shared;

namespace Mimir.Server.Tests.Components.Shared;

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

    [Fact]
    public void BindingAnotherRecord_Disarms()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.Bind(RecordB);

        arming.Armed.ShouldBeFalse();
    }

    [Fact]
    public void RebindingTheSameRecord_StaysArmed()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.Bind(RecordA);

        arming.Armed.ShouldBeTrue();
    }

    [Fact]
    public void ConfirmingWhatIsNotArmed_IsRefused()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);

        arming.TryConfirm().ShouldBeFalse();
    }

    [Fact]
    public void ConfirmingAfterTheSubjectMoved_IsRefused()
    {
        var arming = new ConfirmArming();
        arming.Bind(RecordA);
        arming.Arm();

        arming.Bind(RecordB);

        arming.TryConfirm().ShouldBeFalse();
    }

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
