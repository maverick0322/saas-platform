using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Tenants.GetTenantAccessState;

public enum GetTenantAccessStateStatus
{
    Found = 0,
    NotFound = 1,
    QuotaNotConfigured = 2
}

public sealed record TenantAccessStateSnapshot(
    Guid TenantId,
    string Slug,
    string Name,
    TenantOperationalState State,
    bool MayAuthenticate,
    int MaxActiveAccounts,
    string EvaluatedAtGmt6);

public sealed record GetTenantAccessStateResult(
    GetTenantAccessStateStatus Status,
    TenantAccessStateSnapshot? Tenant)
{
    public static GetTenantAccessStateResult Found(TenantAccessStateSnapshot tenant) =>
        new(GetTenantAccessStateStatus.Found, tenant);

    public static GetTenantAccessStateResult NotFound() =>
        new(GetTenantAccessStateStatus.NotFound, null);

    public static GetTenantAccessStateResult QuotaNotConfigured() =>
        new(GetTenantAccessStateStatus.QuotaNotConfigured, null);
}
