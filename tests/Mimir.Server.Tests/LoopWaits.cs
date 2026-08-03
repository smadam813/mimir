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

    /// <param name="expected">What the loop was waited on to do, read into the timeout message:
    /// a straddle has three waits that can time out and the message is all that tells them apart.
    /// </param>
    public static async Task UntilAsync(
        Func<bool> until, string expected, TimeSpan patience, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!until())
        {
            if (elapsed.Elapsed > patience)
            {
                throw new TimeoutException($"The loop did not {expected} within {patience}.");
            }

            await Task.Delay(Beat, cancellationToken);
        }
    }
}
