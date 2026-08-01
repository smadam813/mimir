using Mimir.Contracts.Health;

namespace Mimir.Server.Components.Health;

internal readonly record struct ModelPull(string Name, int? PercentComplete)
{
    internal static ModelPull? From(OllamaTile ollama)
        => ollama.Models.FirstOrDefault(m => m.State == ModelProvisioningState.Pulling) is { } pulling
            ? new ModelPull(pulling.Name, pulling.PercentComplete)
            : null;
}
