namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Servicio hospedado para el proceso trabajador de Platform.
/// Permanece deshabilitado por defecto y rehúsa ejecutar ciclos mientras no existan adaptadores reales.
/// </summary>
public sealed class ProvisioningWorkerHostedService(
    IServiceScopeFactory scopeFactory,
    IProvisioningStageExecutor stageExecutor,
    ProvisioningWorkerOptions options,
    ILogger<ProvisioningWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();

        if (!options.Enabled)
        {
            logger.LogInformation(
                "Provisioning worker is disabled by default (ProvisioningWorker:Enabled=false). Exiting safely without polling.");
            return;
        }

        if (!stageExecutor.SupportsRealProvisioning)
        {
            logger.LogWarning(
                "Provisioning worker is enabled in configuration, but external provisioning adapters are not configured (SupportsRealProvisioning=false). Refusing to process operations.");
            return;
        }

        var workerId = options.ResolveWorkerId();
        logger.LogInformation(
            "Provisioning worker {WorkerId} started with poll interval {PollIntervalSeconds}s.",
            workerId,
            options.PollIntervalSeconds);

        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<ProvisioningOperationProcessor>();
                await processor.ProcessNextAsync(workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during provisioning worker cycle for {WorkerId}.", workerId);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
