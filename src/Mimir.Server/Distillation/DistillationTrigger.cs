using System.Threading.Channels;

namespace Mimir.Server.Distillation;

/// <summary>
/// §6: Sealing an Episode queues it; this is how the queue's worker hears about it before its
/// poll. Capture pokes it on SessionEnd, the sweep pokes it after re-queueing — always
/// fire-and-forget, since the Seal path must never wait on distillation.
/// </summary>
internal interface IDistillationTrigger
{
    /// <summary>Asks the worker to look at the queue soon. Never blocks.</summary>
    void Request();

    /// <summary>Completes when a look has been requested since the last wait completed.</summary>
    Task WaitAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDistillationTrigger"/>
internal sealed class DistillationTrigger : IDistillationTrigger
{
    // Capacity one, drop-on-full: N Seals while the worker is busy coalesce into one wake-up —
    // the worker drains the whole queue whenever it wakes anyway.
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _signals.Writer.TryWrite(true);

    public async Task WaitAsync(CancellationToken cancellationToken)
        => await _signals.Reader.ReadAsync(cancellationToken);
}
