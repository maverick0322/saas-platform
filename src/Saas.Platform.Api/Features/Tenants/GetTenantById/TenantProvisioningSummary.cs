using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Tenants.GetTenantById;

/// <summary>
/// Representación administrativa del estado técnico y progreso de la operación inicial de aprovisionamiento.
/// </summary>
public sealed record TenantProvisioningSummary(
    Guid OperationId,
    string ProvisioningRequestId,
    ProvisioningOperationStatus Status,
    string StatusCode,
    string InitialAdminFullName,
    string InitialAdminEmail,
    int AttemptCount,
    string? LastErrorCode,
    string? LastErrorSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? NextAttemptAtUtc = null,
    bool ManualInterventionRequired = false,
    ProvisioningStageState InfrastructureStage = ProvisioningStageState.Pending,
    ProvisioningStageState ServiceDatabasesStage = ProvisioningStageState.Pending,
    ProvisioningStageState ClinicalStage = ProvisioningStageState.Pending,
    ProvisioningStageState IdentityStage = ProvisioningStageState.Pending);
