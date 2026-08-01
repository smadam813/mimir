namespace Mimir.Server.Models;

public interface IModelCatalog
{
    Task<IReadOnlyList<string>> ListLocalModelsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken);
}

public readonly record struct ModelPullProgress(string Status, int? PercentComplete);
