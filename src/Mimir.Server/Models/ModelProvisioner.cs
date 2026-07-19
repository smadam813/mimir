using Microsoft.Extensions.Options;
using Mimir.Contracts.Health;
using Mimir.Server.Configuration;
using Mimir.Server.Health;

namespace Mimir.Server.Models;

/// <summary>
/// Spec §12: on startup Mimir provisions the two §11 models. Ollama is usually still booting when
/// we get here, so the first job is waiting for it; the second is pulling whatever is missing.
/// Both are narrated on the health strip, which is the only progress the user gets during a
/// multi-gigabyte first run.
/// </summary>
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

    /// <summary>
    /// Pulls the model at <paramref name="index"/>, republishing the whole strip as progress
    /// arrives. Progress is tracked in <paramref name="statuses"/> rather than read back off the
    /// health state, so the provisioner stays the sole author of its own tile.
    /// </summary>
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
            // One unusable model must not stop the others from provisioning; the tile reports which.
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
        if (statuses.FirstOrDefault(m => m.State == ModelProvisioningState.Pulling) is { } pulling)
        {
            var percent = pulling.PercentComplete is { } value ? $" {value}%" : string.Empty;
            return (HealthTileState.Working, $"Pulling {pulling.Name}{percent}");
        }

        var failed = statuses.Count(m => m.State == ModelProvisioningState.Failed);
        if (failed > 0)
        {
            return (HealthTileState.Degraded, $"{failed} of {statuses.Count} models unavailable");
        }

        return statuses.All(m => m.State == ModelProvisioningState.Ready)
            ? (HealthTileState.Ready, $"Ready · {string.Join(", ", statuses.Select(m => m.Name))}")
            : (HealthTileState.Working, "Provisioning models");
    }

    /// <summary>Ollama reports an untagged model as <c>:latest</c>; compare on the same footing.</summary>
    private static string NormalizeTag(string model) => model.Contains(':') ? model : $"{model}:latest";
}
