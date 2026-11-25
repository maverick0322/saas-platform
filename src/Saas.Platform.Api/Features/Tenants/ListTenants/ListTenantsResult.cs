using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Tenants.ListTenants;

public sealed record TenantListItemSummary(
    Guid Id,
    string PublicId,
    string Name,
    TenantOperationalState OperationalState,
    int? MaxActiveAccounts,
    ProvisioningOperationStatus? ProvisioningStatus,
    bool ManualInterventionRequired,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public bool MayAuthenticate => OperationalState == TenantOperationalState.Activa;
}

public sealed record ListTenantsResult(
    IReadOnlyList<TenantListItemSummary> Items,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages);
