using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Persistence;

namespace Saas.Platform.Api.Features.Tenants.RegisterTenant;

public sealed class RegisterTenantHandler(
    PlatformDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<RegisterTenantHandler> logger)
{
    public async Task<RegisterTenantResult> HandleAsync(
        RegisterTenantCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var utcNow = timeProvider.GetUtcNow().ToUniversalTime();

        var tenantValid = Tenant.TryRegister(
            command.PublicId,
            command.Name,
            command.ContactEmail,
            command.MaxActiveAccounts,
            utcNow,
            out var tenant,
            out var tenantErrors);

        var adminValid = TenantProvisioningOperation.TryValidateInitialAdmin(
            command.InitialAdminFullName,
            command.InitialAdminEmail,
            out _,
            out _,
            out var adminErrors);

        if (!tenantValid || !adminValid || tenant is null)
        {
            var allErrors = new List<TenantValidationError>(tenantErrors.Count + adminErrors.Count);
            allErrors.AddRange(tenantErrors);
            allErrors.AddRange(adminErrors);
            return RegisterTenantResult.ValidationFailed(allErrors);
        }

        if (!TenantProvisioningOperation.TryCreateInitial(
                tenant.Id,
                command.InitialAdminFullName,
                command.InitialAdminEmail,
                utcNow,
                out var operation,
                out var operationErrors)
            || operation is null)
        {
            return RegisterTenantResult.ValidationFailed(operationErrors);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        dbContext.Tenants.Add(tenant);
        dbContext.TenantProvisioningOperations.Add(operation);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (TryMapConflict(ex, tenant.PublicId, out var conflictKind, out var conflictReason))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            DetachTrackedEntities(tenant, operation);

            if (conflictKind == RegisterTenantConflictKind.PublicIdAlreadyExists)
            {
                var existingTenantId = await dbContext.Tenants
                    .AsNoTracking()
                    .Where(existing => existing.PublicId == tenant.PublicId)
                    .Select(existing => (Guid?)existing.Id)
                    .SingleOrDefaultAsync(cancellationToken);

                logger.LogWarning(
                    "Tenant catalog registration rejected due to duplicate PublicId {PublicId} (ExistingTenantId={ExistingTenantId}).",
                    tenant.PublicId,
                    existingTenantId);

                return RegisterTenantResult.PublicIdConflict(conflictReason, existingTenantId);
            }

            logger.LogWarning(
                "Tenant catalog registration rejected due to internal uniqueness conflict for PublicId {PublicId}.",
                tenant.PublicId);

            return RegisterTenantResult.Conflict(conflictReason);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            DetachTrackedEntities(tenant, operation);

            logger.LogWarning(
                ex,
                "Concurrency conflict while registering tenant {PublicId}.",
                tenant.PublicId);

            return RegisterTenantResult.Conflict(
                "A concurrency conflict occurred while registering the tenant.");
        }

        logger.LogInformation(
            "Registered tenant {TenantId} ({PublicId}) in state {OperationalState} with durable provisioning operation {OperationId} ({ProvisioningRequestId}).",
            tenant.Id,
            tenant.PublicId,
            tenant.OperationalState.ToCode(),
            operation.Id,
            operation.ProvisioningRequestId);

        return RegisterTenantResult.Created(TenantDetailsMapper.Map(tenant, operation));
    }

    private void DetachTrackedEntities(Tenant tenant, TenantProvisioningOperation operation)
    {
        dbContext.Entry(operation).State = EntityState.Detached;
        dbContext.Entry(tenant).State = EntityState.Detached;
    }

    private static bool TryMapConflict(
        DbUpdateException exception,
        string publicId,
        out RegisterTenantConflictKind conflictKind,
        out string conflictReason)
    {
        if (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: var constraintName
            })
        {
            switch (constraintName)
            {
                case "ux_tenants_public_id":
                    conflictKind = RegisterTenantConflictKind.PublicIdAlreadyExists;
                    conflictReason = $"A tenant with public identifier '{publicId}' is already registered.";
                    return true;
                case "ux_tenant_provisioning_operations_tenant_id":
                    conflictKind = RegisterTenantConflictKind.InternalConflict;
                    conflictReason = $"An initial provisioning operation already exists for tenant '{publicId}'.";
                    return true;
                case "ux_tenant_provisioning_operations_provisioning_request_id":
                    conflictKind = RegisterTenantConflictKind.InternalConflict;
                    conflictReason = "A provisioning operation with the same request identifier already exists.";
                    return true;
            }
        }

        conflictKind = RegisterTenantConflictKind.None;
        conflictReason = string.Empty;
        return false;
    }
}
