using Mimir.Server.Distillation;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The Seal-to-worker poke on its own. No Postgres: the trigger is a channel, and a pin that
/// skipped on a Docker-less machine would leave the coalescing unguarded exactly where it is
/// cheapest to guard.
/// </summary>
public sealed class DistillationTriggerTests
{
    [Fact]
    public async Task ManyPokesWhileTheWorkerIsBusy_CoalesceIntoOneWakeUp()
    {
        var trigger = new DistillationTrigger();

        trigger.Request();
        trigger.Request();
        trigger.Request();

        await trigger.WaitAsync(TestContext.Current.CancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Should.ThrowAsync<OperationCanceledException>(trigger.WaitAsync(deadline.Token));
    }

    [Fact]
    public async Task APokeAfterTheLastWait_WakesTheWorkerAgain()
    {
        var trigger = new DistillationTrigger();

        trigger.Request();
        await trigger.WaitAsync(TestContext.Current.CancellationToken);
        trigger.Request();

        // Bounded rather than awaited outright: a coalescing bug that swallowed the second poke
        // would otherwise hang to the suite timeout instead of naming itself here.
        await trigger.WaitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
