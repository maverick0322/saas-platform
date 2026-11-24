namespace Saas.Platform.Api.Features.Tenants.RegisterTenant;

public sealed record RegisterTenantCommand(
    string? PublicId,
    string? Name,
    string? ContactEmail,
    string? InitialAdminFullName,
    string? InitialAdminEmail);
