namespace Saas.Platform.Api.Features.Tenants.ListTenants;

public sealed record ListTenantsQuery(
    int Page = ListTenantsQuery.DefaultPage,
    int PageSize = ListTenantsQuery.DefaultPageSize)
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
}
