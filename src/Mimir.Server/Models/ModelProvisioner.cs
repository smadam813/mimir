using Microsoft.Extensions.Options;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Health;

namespace Mimir.Server.Models;

public sealed class ModelProvisioner(
    IModelCatalog catalog,
    IHealthState health,
    IOptions<ModelOptions> options,
    TimeProvider timeProvider,
    ILogger<ModelProvisioner> logger)
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly ModelOptions _options = options.Value;

    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var statuses = _options.Provisioned
            .Select(name => new ModelStatus { Name = name, State = ModelProvisioningState.Pending })
            .ToArray();
        Publish(statuses);

        var present = (await WaitForOllamaAsync(statuses, cancellationToken))
            .Select(NormalizeTag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < statuses.Length; i++)
        {
            if (present.Contains(NormalizeTag(statuses[i].Name)))
            {
                statuses[i] = statuses[i] with { State = ModelProvisioningState.Ready };
            }
            else
            {
                await PullAsync(statuses, i, cancellationToken);
            }

            Publish(statuses);
        }
    }

    private async Task PullAsync(ModelStatus[] statuses, int index, CancellationToken cancellationToken)
    {
        var model = statuses[index];
        logger.LogInformation("Model {Model} is not present; pulling it", model.Name);

        try
        {
            statuses[index] = model with { State = ModelProvisioningState.Pulling };
            await foreach (var progress in catalog.PullAsync(model.Name, cancellationToken))
            {
                statuses[index] = statuses[index] with { PercentComplete = progress.PercentComplete };
                Publish(statuses);
            }

            logger.LogInformation("Provisioned model {Model}", model.Name);
            statuses[index] = model with { State = ModelProvisioningState.Ready };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to provision model {Model}", model.Name);
            statuses[index] = model with { State = ModelProvisioningState.Failed, Error = ex.Message };
        }
    }

    private async Task<IReadOnlyList<string>> WaitForOllamaAsync(
        IReadOnlyList<ModelStatus> statuses,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await catalog.ListLocalModelsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Ollama is not reachable at {Endpoint}; retrying in {RetryInterval}",
                    _options.Endpoint,
                    RetryInterval);

                health.Update(snapshot => snapshot with
                {
                    Ollama = new OllamaTile
                    {
                        State = HealthTileState.Degraded,
                        Summary = $"Cannot reach Ollama at {_options.Endpoint} — retrying",
                        Models = statuses,
                    },
                });

                await Task.Delay(RetryInterval, timeProvider, cancellationToken);
            }
        }
    }

    private void Publish(IReadOnlyList<ModelStatus> statuses)
    {
        var (state, summary) = Describe(statuses);
        health.Update(snapshot => snapshot with
        {
            Ollama = new OllamaTile { State = state, Summary = summary, Models = [.. statuses] },
        });
    }

    private static (HealthTileState State, string Summary) Describe(IReadOnlyList<ModelStatus> statuses)
    {
        var failed = statuses.Count(m => m.State == ModelProvisioningState.Failed);

        if (statuses.FirstOrDefault(m => m.State == ModelProvisioningState.Pulling) is { } pulling)
        {
            var percent = pulling.PercentComplete is { } value ? $" {value}%" : string.Empty;
            var progress = $"Pulling {pulling.Name}{percent}";

            return failed > 0
                ? (HealthTileState.Degraded, $"{progress} · {Unavailable(failed, statuses.Count)}")
                : (HealthTileState.Working, progress);
        }

        if (failed > 0)
        {
            return (HealthTileState.Degraded, Unavailable(failed, statuses.Count));
        }

        return statuses.All(m => m.State == ModelProvisioningState.Ready)
            ? (HealthTileState.Ready, $"Ready · {string.Join(", ", statuses.Select(m => m.Name))}")
            : (HealthTileState.Working, "Provisioning models");
    }

    private static string Unavailable(int failed, int total) => $"{failed} of {total} models unavailable";

    private static string NormalizeTag(string model) => model.Contains(':') ? model : $"{model}:latest";
}
