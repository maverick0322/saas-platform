using Microsoft.EntityFrameworkCore;
using Saas.Platform.Api.Persistence;

namespace Saas.Platform.Api.Features.Tenants.GetTenantById;

public sealed class GetTenantByIdHandler(PlatformDbContext dbContext)
{
    public async Task<TenantDetails?> HandleAsync(GetTenantByIdQuery query, CancellationToken cancellationToken)
    {
        if (query.TenantId == Guid.Empty)
        {
            return null;
        }

        var row = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == query.TenantId)
            .GroupJoin(
                dbContext.TenantProvisioningOperations.AsNoTracking(),
                tenant => tenant.Id,
                operation => operation.TenantId,
                (tenant, operations) => new { tenant, operations })
            .SelectMany(
                joined => joined.operations.DefaultIfEmpty(),
                (joined, operation) => new { Tenant = joined.tenant, Operation = operation })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : TenantDetailsMapper.Map(row.Tenant, row.Operation);
    }
}
