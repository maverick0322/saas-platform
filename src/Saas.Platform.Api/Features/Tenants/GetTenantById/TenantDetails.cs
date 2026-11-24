using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Tenants.GetTenantById;

/// <summary>
/// Proyección administrativa del tenant y su estado operativo/aprovisionamiento en Platform.
/// </summary>
public sealed record TenantDetails(
    Guid Id,
    string PublicId,
    string Name,
    string ContactEmail,
    TenantOperationalState OperationalState,
    string OperationalStateCode,
    bool MayAuthenticate,
    int? MaxActiveAccounts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    TenantProvisioningSummary? InitialProvisioning);

internal static class TenantDetailsMapper
{
    public static TenantDetails Map(Tenant tenant, TenantProvisioningOperation? operation) =>
        new(
            tenant.Id,
            tenant.PublicId,
            tenant.Name,
            tenant.ContactEmail,
            tenant.OperationalState,
            tenant.OperationalState.ToCode(),
            tenant.MayAuthenticate,
            tenant.MaxActiveAccounts,
            tenant.CreatedAtUtc,
            tenant.UpdatedAtUtc,
            operation is null
                ? null
                : new TenantProvisioningSummary(
                    operation.Id,
                    operation.ProvisioningRequestId,
                    operation.Status,
                    operation.Status.ToCode(),
                    operation.InitialAdminFullName,
                    operation.InitialAdminEmail,
                    operation.AttemptCount,
                    operation.LastErrorCode,
                    operation.LastErrorSummary,
                    operation.CreatedAtUtc,
                    operation.UpdatedAtUtc,
                    operation.LastAttemptAtUtc,
                    operation.CompletedAtUtc,
                    operation.NextAttemptAtUtc,
                    operation.ManualInterventionRequired,
                    operation.InfrastructureStageState,
                    operation.ServiceDatabasesStageState,
                    operation.ClinicalStageState,
                    operation.IdentityStageState));
}
