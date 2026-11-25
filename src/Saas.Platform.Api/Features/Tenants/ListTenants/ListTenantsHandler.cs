using Microsoft.EntityFrameworkCore;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Persistence;

namespace Saas.Platform.Api.Features.Tenants.ListTenants;

/// <summary>
/// Consulta de solo lectura y paginación acotada para listar el catálogo administrativo global de clínicas.
/// </summary>
public sealed class ListTenantsHandler(PlatformDbContext dbContext)
{
    public async Task<ListTenantsResult> HandleAsync(
        ListTenantsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Page, "Page must be at least 1.");
        }

        if (query.PageSize is < 1 or > ListTenantsQuery.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.PageSize,
                $"PageSize must be between 1 and {ListTenantsQuery.MaxPageSize}.");
        }

        var totalCount = await dbContext.Tenants
            .AsNoTracking()
            .LongCountAsync(cancellationToken);

        var totalPages = totalCount == 0
            ? 0
            : (int)Math.Min(int.MaxValue, (totalCount + query.PageSize - 1L) / query.PageSize);

        var offset = ((long)query.Page - 1L) * query.PageSize;
        if (totalCount == 0 || offset >= totalCount || offset > int.MaxValue)
        {
            return new ListTenantsResult([], query.Page, query.PageSize, totalCount, totalPages);
        }

        var items = await dbContext.Tenants
            .AsNoTracking()
            .GroupJoin(
                dbContext.TenantProvisioningOperations.AsNoTracking(),
                tenant => tenant.Id,
                operation => operation.TenantId,
                (tenant, operations) => new { tenant, operations })
            .SelectMany(
                joined => joined.operations.DefaultIfEmpty(),
                (joined, operation) => new
                {
                    joined.tenant.Id,
                    joined.tenant.PublicId,
                    joined.tenant.Name,
                    joined.tenant.OperationalState,
                    joined.tenant.MaxActiveAccounts,
                    ProvisioningStatus = operation != null ? operation.Status : (ProvisioningOperationStatus?)null,
                    ManualInterventionRequired = operation != null && operation.ManualInterventionRequired,
                    joined.tenant.CreatedAtUtc,
                    joined.tenant.UpdatedAtUtc
                })
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.Id)
            .Skip((int)offset)
            .Take(query.PageSize)
            .Select(row => new TenantListItemSummary(
                row.Id,
                row.PublicId,
                row.Name,
                row.OperationalState,
                row.MaxActiveAccounts,
                row.ProvisioningStatus,
                row.ManualInterventionRequired,
                row.CreatedAtUtc,
                row.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new ListTenantsResult(items, query.Page, query.PageSize, totalCount, totalPages);
    }
}
