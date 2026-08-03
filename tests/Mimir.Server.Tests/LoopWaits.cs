using System.Diagnostics;

namespace Mimir.Server.Tests;

/// <summary>
/// Waiting on a hosted loop that publishes no health tile, so the only signal a test has is what
/// the loop logged. <see cref="HealthTileWaits"/> is the same wait for the loops that do publish
/// one, and is the better signal wherever it is available.
/// </summary>
public static class LoopWaits
{
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(20);

    public static async Task UntilAsync(Func<bool> until, TimeSpan patience, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!until())
        {
            if (elapsed.Elapsed > patience)
            {
                throw new TimeoutException($"The loop did not reach the expected state within {patience}.");
            }

            await Task.Delay(Beat, cancellationToken);
        }
    }
}
