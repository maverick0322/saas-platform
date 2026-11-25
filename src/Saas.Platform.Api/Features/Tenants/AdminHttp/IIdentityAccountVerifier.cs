namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

internal enum IdentityAccountVerificationOutcome
{
    Verified = 0,
    Unauthorized = 1,
    Forbidden = 2,
    Unavailable = 3
}

internal interface IIdentityAccountVerifier
{
    Task<IdentityAccountVerificationOutcome> VerifySuperAdminAsync(
        Guid expectedSubjectId,
        string bearerToken,
        CancellationToken cancellationToken);
}
