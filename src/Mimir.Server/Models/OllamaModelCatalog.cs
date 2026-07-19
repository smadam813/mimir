using System.Runtime.CompilerServices;
using OllamaSharp;
using OllamaSharp.Models;

namespace Mimir.Server.Models;

/// <inheritdoc cref="IModelCatalog"/>
internal sealed class OllamaModelCatalog(IOllamaApiClient client) : IModelCatalog
{
    public async Task<IReadOnlyList<string>> ListLocalModelsAsync(CancellationToken cancellationToken)
    {
        var models = await client.ListLocalModelsAsync(cancellationToken);
        return [.. models.Select(model => model.Name)];
    }

    public async IAsyncEnumerable<ModelPullProgress> PullAsync(
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new PullModelRequest { Model = model };

        await foreach (var response in client.PullModelAsync(request, cancellationToken))
        {
            if (response is not null)
            {
                yield return new ModelPullProgress(response.Status ?? string.Empty, ToPercent(response));
            }
        }
    }

    /// <summary>Ollama omits the byte totals on the manifest and verify phases; report no percentage there.</summary>
    private static int? ToPercent(PullModelResponse response)
        => response.Total > 0 ? (int)Math.Clamp(Math.Round(response.Percent), 0, 100) : null;
}
