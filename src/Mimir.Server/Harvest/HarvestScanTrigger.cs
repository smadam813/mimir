using System.Threading.Channels;

namespace Mimir.Server.Harvest;

internal interface IHarvestScanTrigger
{
    void Request();

    Task WaitAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IHarvestScanTrigger"/>
internal sealed class HarvestScanTrigger : IHarvestScanTrigger
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _signals.Writer.TryWrite(true);

    public async Task WaitAsync(CancellationToken cancellationToken)
        => await _signals.Reader.ReadAsync(cancellationToken);
}
