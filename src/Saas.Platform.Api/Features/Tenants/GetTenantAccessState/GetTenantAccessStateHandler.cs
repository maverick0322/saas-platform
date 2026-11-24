using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Persistence;

namespace Saas.Platform.Api.Features.Tenants.GetTenantAccessState;

/// <summary>
/// Consulta ligera AsNoTracking sobre el catálogo global de Platform para exponer estado operativo
/// y cuota por clínica sin revelar datos de contacto, credenciales, operaciones ni datos clínicos.
/// </summary>
public sealed class GetTenantAccessStateHandler(
    PlatformDbContext dbContext,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan GmtMinus6Offset = TimeSpan.FromHours(-6);

    public async Task<GetTenantAccessStateResult> HandleAsync(
        GetTenantAccessStateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tenants = dbContext.Tenants.AsNoTracking();

        var row = query.TenantId is { } tenantId
            ? await tenants
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => new
                {
                    tenant.Id,
                    tenant.PublicId,
                    tenant.Name,
                    tenant.OperationalState,
                    tenant.MaxActiveAccounts
                })
                .SingleOrDefaultAsync(cancellationToken)
            : await tenants
                .Where(tenant => tenant.PublicId == query.Slug)
                .Select(tenant => new
                {
                    tenant.Id,
                    tenant.PublicId,
                    tenant.Name,
                    tenant.OperationalState,
                    tenant.MaxActiveAccounts
                })
                .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return GetTenantAccessStateResult.NotFound();
        }

        if (row.MaxActiveAccounts is null || row.MaxActiveAccounts.Value < Tenant.MinMaxActiveAccounts)
        {
            return GetTenantAccessStateResult.QuotaNotConfigured();
        }

        var evaluatedAtGmt6 = timeProvider
            .GetUtcNow()
            .ToOffset(GmtMinus6Offset)
            .ToString("O", CultureInfo.InvariantCulture);

        var mayAuthenticate = row.OperationalState == TenantOperationalState.Activa;

        return GetTenantAccessStateResult.Found(new TenantAccessStateSnapshot(
            TenantId: row.Id,
            Slug: row.PublicId,
            Name: row.Name,
            State: row.OperationalState,
            MayAuthenticate: mayAuthenticate,
            MaxActiveAccounts: row.MaxActiveAccounts.Value,
            EvaluatedAtGmt6: evaluatedAtGmt6));
    }
}
