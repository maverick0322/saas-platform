namespace Saas.Platform.Api.Features.Tenants.GetTenantAccessState;

public sealed class GetTenantAccessStateQuery
{
    private GetTenantAccessStateQuery(Guid? tenantId, string? slug)
    {
        TenantId = tenantId;
        Slug = slug;
    }

    public Guid? TenantId { get; }

    public string? Slug { get; }

    public static GetTenantAccessStateQuery ByTenantId(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must be a non-empty UUID.", nameof(tenantId));
        }

        return new GetTenantAccessStateQuery(tenantId, null);
    }

    public static GetTenantAccessStateQuery BySlug(string normalizedSlug)
    {
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            throw new ArgumentException("Slug is required.", nameof(normalizedSlug));
        }

        return new GetTenantAccessStateQuery(null, normalizedSlug);
    }
}
