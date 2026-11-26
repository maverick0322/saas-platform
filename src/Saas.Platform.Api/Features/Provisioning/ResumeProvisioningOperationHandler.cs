namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Comando interno controlado para reanudar una operación específica detenida por <c>ADAPTER_NOT_CONFIGURED</c>,
/// re-habilitando su adquisición ordinaria mediante el mecanismo de lease sin omitirlo (PLAT-05B).
/// </summary>
public sealed class ResumeProvisioningOperationHandler(
    ProvisioningLeaseStore leaseStore,
    IProvisioningStageExecutor stageExecutor,
    TimeProvider timeProvider,
    ProvisioningWorkerOptions options,
    ILogger<ResumeProvisioningOperationHandler> logger)
{
    public async Task<ResumeProvisioningOperationResult> HandleAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        options.Validate();

        if (!stageExecutor.SupportsRealProvisioning)
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.NextStageAdapterNotConfigured,
                operationId,
                Summary: "Real provisioning adapter is not enabled in current configuration.");
        }

        var result = await leaseStore.TryResumeAdapterStoppedOperationAsync(
            operationId,
            timeProvider.GetUtcNow(),
            options.MaxTotalAttempts,
            stageExecutor.SupportsStage,
            cancellationToken);

        if (result.Resumed)
        {
            logger.LogInformation(
                "Provisioning operation {OperationId} (TenantId={TenantId}, AttemptCount={AttemptCount}) re-queued for stage {NextStage}.",
                result.OperationId,
                result.TenantId,
                result.AttemptCount,
                result.NextStage);
        }
        else
        {
            logger.LogWarning(
                "Controlled resumption rejected for provisioning operation {OperationId} with outcome {Outcome}.",
                result.OperationId,
                result.Outcome);
        }

        return result;
    }
}
