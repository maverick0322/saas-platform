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

        if (!Tenant.TryRegister(
                command.PublicId,
                command.Name,
                command.ContactEmail,
                timeProvider.GetUtcNow(),
                out var tenant,
                out var validationErrors)
            || tenant is null)
        {
            return RegisterTenantResult.ValidationFailed(validationErrors);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        dbContext.Tenants.Add(tenant);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsPublicIdUniqueViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.Entry(tenant).State = EntityState.Detached;

            logger.LogWarning(
                "Tenant catalog registration rejected due to duplicate PublicId {PublicId}.",
                tenant.PublicId);

            return RegisterTenantResult.Conflict(
                $"A tenant with public identifier '{tenant.PublicId}' is already registered.");
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.Entry(tenant).State = EntityState.Detached;

            logger.LogWarning(
                ex,
                "Concurrency conflict while registering tenant {PublicId}.",
                tenant.PublicId);

            return RegisterTenantResult.Conflict(
                "A concurrency conflict occurred while registering the tenant.");
        }

        logger.LogInformation(
            "Registered tenant administrative catalog entry {TenantId} ({PublicId}).",
            tenant.Id,
            tenant.PublicId);

        return RegisterTenantResult.Created(new TenantDetails(
            tenant.Id,
            tenant.PublicId,
            tenant.Name,
            tenant.ContactEmail,
            tenant.CreatedAtUtc,
            tenant.UpdatedAtUtc));
    }

    private static bool IsPublicIdUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_tenants_public_id"
        };
}
