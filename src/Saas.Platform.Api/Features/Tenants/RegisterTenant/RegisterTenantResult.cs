using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantById;

namespace Saas.Platform.Api.Features.Tenants.RegisterTenant;

public enum RegisterTenantStatus
{
    Created = 0,
    ValidationFailed = 1,
    Conflict = 2
}

public enum RegisterTenantConflictKind
{
    None = 0,
    PublicIdAlreadyExists = 1,
    InternalConflict = 2
}

public sealed record RegisterTenantResult(
    RegisterTenantStatus Status,
    TenantDetails? Tenant,
    IReadOnlyList<TenantValidationError> ValidationErrors,
    string? ConflictReason,
    RegisterTenantConflictKind ConflictKind = RegisterTenantConflictKind.None,
    Guid? ExistingTenantId = null)
{
    public static RegisterTenantResult Created(TenantDetails tenant) =>
        new(RegisterTenantStatus.Created, tenant, [], null, RegisterTenantConflictKind.None, null);

    public static RegisterTenantResult ValidationFailed(IReadOnlyList<TenantValidationError> errors) =>
        new(RegisterTenantStatus.ValidationFailed, null, errors, null, RegisterTenantConflictKind.None, null);

    public static RegisterTenantResult Conflict(string reason) =>
        new(RegisterTenantStatus.Conflict, null, [], reason, RegisterTenantConflictKind.InternalConflict, null);

    public static RegisterTenantResult PublicIdConflict(string reason, Guid? existingTenantId) =>
        new(
            RegisterTenantStatus.Conflict,
            null,
            [],
            reason,
            RegisterTenantConflictKind.PublicIdAlreadyExists,
            existingTenantId);
}
