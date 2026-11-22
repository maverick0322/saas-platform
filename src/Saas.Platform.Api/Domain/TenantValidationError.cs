namespace Saas.Platform.Api.Domain;

public sealed record TenantValidationError(string Field, string Code, string Message);
