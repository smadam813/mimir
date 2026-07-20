using System.Runtime.CompilerServices;
using Mimir.Server.Models;

namespace Mimir.Server.Tests.Models;

/// <summary>
/// A scriptable stand-in for Ollama. Every method records its calls so tests can assert that a
/// model already present was <em>not</em> re-pulled.
/// </summary>
internal sealed class FakeModelCatalog : IModelCatalog
{
    private readonly Queue<Func<IReadOnlyList<string>>> _listResponses = new();

    public List<string> LocalModels { get; } = [];

    public List<string> PullRequests { get; } = [];

    public int ListCalls { get; private set; }

    /// <summary>Progress each pull emits, keyed by model name. Defaults to a single 100% tick.</summary>
    public Dictionary<string, IReadOnlyList<ModelPullProgress>> PullProgress { get; } = [];

    /// <summary>Models whose pull throws instead of completing, keyed by model name.</summary>
    public Dictionary<string, Exception> PullFailures { get; } = [];

    /// <summary>Queues one outcome for the next <see cref="ListLocalModelsAsync"/> call.</summary>
    public FakeModelCatalog EnqueueListFailure(Exception exception)
    {
        _listResponses.Enqueue(() => throw exception);
        return this;
    }

    public Task<IReadOnlyList<string>> ListLocalModelsAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        cancellationToken.ThrowIfCancellationRequested();

        return _listResponses.Count > 0
            ? Task.FromResult(_listResponses.Dequeue()())
            : Task.FromResult<IReadOnlyList<string>>([.. LocalModels]);
    }

    public async IAsyncEnumerable<ModelPullProgress> PullAsync(
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PullRequests.Add(model);

        if (PullFailures.TryGetValue(model, out var failure))
        {
            throw failure;
        }

        var progress = PullProgress.TryGetValue(model, out var scripted)
            ? scripted
            : [new ModelPullProgress("success", 100)];

        foreach (var tick in progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return tick;
        }

        LocalModels.Add(model);
        await Task.CompletedTask;
    }
}
