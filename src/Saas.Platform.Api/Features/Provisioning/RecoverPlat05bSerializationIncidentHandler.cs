using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Comando de mantenimiento excepcional y temporal (PLAT-05B-FIX-2) para reencolar únicamente la operación
/// del laboratorio afectada por el falso positivo de serialización de <c>DatabaseRole</c> en CloudNativePG 1.30.2.
/// TEMPORAL: debe retirarse antes de promocionar a <c>main</c>.
/// </summary>
public sealed class RecoverPlat05bSerializationIncidentHandler(
    ProvisioningLeaseStore leaseStore,
    IProvisioningStageExecutor stageExecutor,
    TimeProvider timeProvider,
    ProvisioningWorkerOptions options,
    ILogger<RecoverPlat05bSerializationIncidentHandler> logger)
{
    public async Task<RecoverPlat05bSerializationResult> HandleAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        options.Validate();

        if (!stageExecutor.SupportsRealProvisioning || !stageExecutor.SupportsStage(ProvisioningStage.ServiceDatabases))
        {
            var denied = new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.NextStageAdapterNotConfigured,
                operationId,
                Summary: "Real CloudNativePG ServiceDatabases adapter is not enabled in current configuration.");

            logger.LogWarning(
                "Temporary PLAT-05B serialization recovery rejected for operation {OperationId} with outcome {Outcome}.",
                denied.OperationId,
                denied.Outcome);

            return denied;
        }

        var result = await leaseStore.TryRecoverPlat05bSerializationIncidentAsync(
            operationId,
            timeProvider.GetUtcNow(),
            options.MaxTotalAttempts,
            supportsServiceDatabases: true,
            cancellationToken);

        if (result.Recovered)
        {
            logger.LogInformation(
                "Temporary PLAT-05B serialization recovery re-queued operation {OperationId} (TenantId={TenantId}, AttemptCount={AttemptCount}) for ordinary Worker lease acquisition on ServiceDatabases.",
                result.OperationId,
                result.TenantId,
                result.AttemptCount);
        }
        else
        {
            logger.LogWarning(
                "Temporary PLAT-05B serialization recovery rejected for operation {OperationId} with outcome {Outcome}.",
                result.OperationId,
                result.Outcome);
        }

        return result;
    }
}
