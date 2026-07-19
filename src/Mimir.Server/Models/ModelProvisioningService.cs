namespace Mimir.Server.Models;

/// <summary>
/// Runs <see cref="ModelProvisioner"/> once at startup, off the critical path so the UI is
/// serving (and showing the Ollama tile) while the models are still downloading.
/// </summary>
internal sealed class ModelProvisioningService(
    ModelProvisioner provisioner,
    ILogger<ModelProvisioningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await provisioner.ProvisionAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Model provisioning abandoned because the host is shutting down");
        }
    }
}
