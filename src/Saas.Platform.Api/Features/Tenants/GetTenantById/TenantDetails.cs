namespace Saas.Platform.Api.Features.Tenants.GetTenantById;

/// <summary>
/// Proyección de lectura del registro administrativo de un tenant en Platform.
/// Refleja exclusivamente la existencia del registro en el catálogo global, no habilitación operativa.
/// </summary>
public sealed record TenantDetails(
    Guid Id,
    string PublicId,
    string Name,
    string ContactEmail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
