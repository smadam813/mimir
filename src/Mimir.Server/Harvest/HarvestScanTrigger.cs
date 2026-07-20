using System.Threading.Channels;

namespace Mimir.Server.Harvest;

/// <summary>
/// Spec §5: every SessionEnd asks for an opportunistic scan between the interval ticks. Capture
/// pokes this; the Harvester waits on it alongside its timer.
/// </summary>
internal interface IHarvestScanTrigger
{
    /// <summary>Asks for a scan soon. Never blocks — this sits on the SessionEnd hook path.</summary>
    void Request();

    /// <summary>Completes when a scan has been requested since the last wait completed.</summary>
    Task WaitAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IHarvestScanTrigger"/>
internal sealed class HarvestScanTrigger : IHarvestScanTrigger
{
    // Capacity one, drop-on-full: N SessionEnds while a scan runs coalesce into one rescan —
    // the scan that follows sees all of their files anyway.
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _signals.Writer.TryWrite(true);

    public async Task WaitAsync(CancellationToken cancellationToken)
        => await _signals.Reader.ReadAsync(cancellationToken);
}
