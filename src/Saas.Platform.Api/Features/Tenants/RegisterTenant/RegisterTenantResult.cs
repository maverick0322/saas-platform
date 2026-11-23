using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantById;

namespace Saas.Platform.Api.Features.Tenants.RegisterTenant;

public enum RegisterTenantStatus
{
    Created = 0,
    ValidationFailed = 1,
    Conflict = 2
}

public sealed record RegisterTenantResult(
    RegisterTenantStatus Status,
    TenantDetails? Tenant,
    IReadOnlyList<TenantValidationError> ValidationErrors,
    string? ConflictReason)
{
    public static RegisterTenantResult Created(TenantDetails tenant) =>
        new(RegisterTenantStatus.Created, tenant, [], null);

    public static RegisterTenantResult ValidationFailed(IReadOnlyList<TenantValidationError> errors) =>
        new(RegisterTenantStatus.ValidationFailed, null, errors, null);

    public static RegisterTenantResult Conflict(string reason) =>
        new(RegisterTenantStatus.Conflict, null, [], reason);
}
