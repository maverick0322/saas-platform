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

        return await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == query.TenantId)
            .Select(tenant => new TenantDetails(
                tenant.Id,
                tenant.PublicId,
                tenant.Name,
                tenant.ContactEmail,
                tenant.CreatedAtUtc,
                tenant.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
