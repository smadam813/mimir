using Mimir.Server.Harvest;

namespace Mimir.Server.Tests.Harvest;

public class HarvestScanTriggerTests
{
    [Fact]
    public async Task ARequestBeforeTheWait_CompletesIt()
    {
        var trigger = new HarvestScanTrigger();

        trigger.Request();

        await trigger.WaitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RequestsWhileNooneWaits_CoalesceIntoOneScan()
    {
        // Ten sessions ending during one scan mean one rescan, not ten: the scan that follows
        // sees all of their files anyway.
        var trigger = new HarvestScanTrigger();
        for (var i = 0; i < 10; i++)
        {
            trigger.Request();
        }

        await trigger.WaitAsync(TestContext.Current.CancellationToken);

        var second = trigger.WaitAsync(TestContext.Current.CancellationToken);
        second.IsCompleted.ShouldBeFalse("the burst must have collapsed into a single signal");
    }

    [Fact]
    public async Task AWaitWithNoRequest_Blocks()
    {
        var trigger = new HarvestScanTrigger();

        var wait = trigger.WaitAsync(TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        wait.IsCompleted.ShouldBeFalse();
    }
}
