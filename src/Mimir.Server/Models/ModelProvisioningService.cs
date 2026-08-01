namespace Mimir.Server.Models;

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
