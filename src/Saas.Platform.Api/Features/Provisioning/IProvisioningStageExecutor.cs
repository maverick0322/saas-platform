using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Provisioning;

public sealed record ProvisioningStageContext(
    Guid OperationId,
    Guid TenantId,
    string TenantPublicId,
    string TenantName,
    string ProvisioningRequestId,
    string InitialAdminFullName,
    string InitialAdminEmail,
    ProvisioningStage Stage,
    bool RequiresVerification,
    int AttemptCount,
    string? InfrastructureResourceRef,
    string? ClinicalMigrationVersion,
    string? InitialAdminAccountId);

public enum ProvisioningStageOutcomeKind
{
    StageVerified = 1,
    SimulatedOnly = 2,
    TransientFailure = 3,
    TerminalFailure = 4,
    UncertainTimeout = 5,
    AdapterNotConfigured = 6
}

public sealed record ProvisioningStageResult(
    ProvisioningStageOutcomeKind Outcome,
    string? ErrorCode = null,
    string? ErrorSummary = null,
    string? TechnicalReference = null);

/// <summary>
/// Puerto acotado para ejecutar o verificar una etapa de aprovisionamiento fuera de transacciones PostgreSQL.
/// En PLAT-02C no existen adaptadores reales habilitados por defecto.
/// </summary>
public interface IProvisioningStageExecutor
{
    bool SupportsRealProvisioning { get; }

    bool SupportsStage(ProvisioningStage stage) => false;

    void ValidateStartupConfiguration()
    {
    }

    Task<ProvisioningStageResult> ExecuteStageAsync(
        ProvisioningStageContext context,
        CancellationToken cancellationToken);
}
