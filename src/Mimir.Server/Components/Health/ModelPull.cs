using Mimir.Contracts.Health;

namespace Mimir.Server.Components.Health;

/// <summary>
/// The one model <c>ModelPullChip</c> names while Ollama provisions (#90). The §11 models are
/// pulled one after another, so at most one is ever in flight; anything already Ready, still
/// Pending or Failed outright is not a pull and reports nothing.
/// <see cref="PercentComplete"/> is null when Ollama reports no total — the header then names the
/// model without claiming a figure it does not have, the way the Storage tile states emptiness
/// rather than inferring it. Pure, so its pins need no database.
/// </summary>
internal readonly record struct ModelPull(string Name, int? PercentComplete)
{
    /// <summary>
    /// The model being pulled right now, or null when none is. "At most one" is
    /// <c>ModelProvisioner</c>'s sequential loop, not something <see cref="OllamaTile"/> enforces,
    /// so the choice if two ever sat at Pulling together is deliberate and pinned: the first the
    /// tile lists, which is the order §11 declares them in. The Health popover beside the chip
    /// lists every model with its own state, so nothing is hidden by taking one for the header.
    /// </summary>
    internal static ModelPull? From(OllamaTile ollama)
        => ollama.Models.FirstOrDefault(m => m.State == ModelProvisioningState.Pulling) is { } pulling
            ? new ModelPull(pulling.Name, pulling.PercentComplete)
            : null;
}
