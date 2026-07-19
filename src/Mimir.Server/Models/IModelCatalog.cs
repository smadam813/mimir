namespace Mimir.Server.Models;

/// <summary>
/// The slice of Ollama's native API that startup provisioning needs (spec §2 — the reason
/// OllamaSharp's native surface is used rather than its OpenAI-compatible one). Inference itself
/// never comes through here; that goes through the Microsoft.Extensions.AI abstractions.
/// </summary>
public interface IModelCatalog
{
    /// <summary>Models already present on the Ollama host, tag included.</summary>
    Task<IReadOnlyList<string>> ListLocalModelsAsync(CancellationToken cancellationToken);

    /// <summary>Downloads <paramref name="model"/>, reporting progress as it streams in.</summary>
    IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken);
}

/// <param name="Status">Ollama's own status line, e.g. <c>pulling manifest</c>.</param>
/// <param name="PercentComplete">0-100 once a total size is known, otherwise null.</param>
public readonly record struct ModelPullProgress(string Status, int? PercentComplete);
