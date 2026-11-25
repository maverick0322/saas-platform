using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

/// <summary>
/// Cliente HTTP de solo autorización para confirmar vigencia y rol global en <c>GET /v1/auth/me</c> de Identity
/// antes de mutaciones administrativas sensibles (<c>D-HTTP-01</c>). Falla cerrado sin filtrar cuerpos crudos ni JWTs.
/// </summary>
internal sealed class IdentityMeAccountVerifier(
    HttpClient httpClient,
    ILogger<IdentityMeAccountVerifier> logger) : IIdentityAccountVerifier
{
    private const string MeRelativePath = "v1/auth/me";

    public async Task<IdentityAccountVerificationOutcome> VerifySuperAdminAsync(
        Guid expectedSubjectId,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        if (expectedSubjectId == Guid.Empty || string.IsNullOrWhiteSpace(bearerToken))
        {
            return IdentityAccountVerificationOutcome.Unauthorized;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, MeRelativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Identity /v1/auth/me verification timed out for SuperAdmin subject {SubjectId}. Failing closed.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "Identity /v1/auth/me verification encountered a transport error for SuperAdmin subject {SubjectId}. Failing closed.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    return IdentityAccountVerificationOutcome.Unauthorized;
                case HttpStatusCode.Forbidden:
                    return IdentityAccountVerificationOutcome.Forbidden;
                case HttpStatusCode.OK:
                    break;
                default:
                    logger.LogWarning(
                        "Identity /v1/auth/me returned unexpected status {StatusCode} for SuperAdmin subject {SubjectId}. Failing closed.",
                        (int)response.StatusCode,
                        expectedSubjectId);
                    return IdentityAccountVerificationOutcome.Unavailable;
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return EvaluateProfile(document.RootElement, expectedSubjectId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "Timed out reading Identity /v1/auth/me payload for SuperAdmin subject {SubjectId}. Failing closed.",
                    expectedSubjectId);
                return IdentityAccountVerificationOutcome.Unavailable;
            }
            catch (JsonException)
            {
                logger.LogWarning(
                    "Identity /v1/auth/me returned malformed JSON for SuperAdmin subject {SubjectId}. Failing closed.",
                    expectedSubjectId);
                return IdentityAccountVerificationOutcome.Unavailable;
            }
        }
    }

    private IdentityAccountVerificationOutcome EvaluateProfile(JsonElement root, Guid expectedSubjectId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            logger.LogWarning(
                "Identity /v1/auth/me returned a non-object JSON payload for SuperAdmin subject {SubjectId}.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (!root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var profileId)
            || profileId == Guid.Empty)
        {
            logger.LogWarning(
                "Identity /v1/auth/me payload missed or had invalid 'id' for SuperAdmin subject {SubjectId}.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (profileId != expectedSubjectId)
        {
            logger.LogWarning(
                "Identity /v1/auth/me returned subject mismatch (expected {ExpectedSubjectId}, actual {ActualSubjectId}).",
                expectedSubjectId,
                profileId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (!root.TryGetProperty("tenantId", out var tenantIdElement)
            || tenantIdElement.ValueKind != JsonValueKind.Null)
        {
            logger.LogWarning(
                "Identity /v1/auth/me returned missing or non-null tenantId for global SuperAdmin subject {SubjectId}.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (!root.TryGetProperty("active", out var activeElement)
            || (activeElement.ValueKind != JsonValueKind.True && activeElement.ValueKind != JsonValueKind.False))
        {
            logger.LogWarning(
                "Identity /v1/auth/me payload missed or had non-boolean 'active' for SuperAdmin subject {SubjectId}.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (!root.TryGetProperty("role", out var roleElement)
            || roleElement.ValueKind != JsonValueKind.String
            || !IdentityClosedRoles.IsKnown(roleElement.GetString()))
        {
            logger.LogWarning(
                "Identity /v1/auth/me payload missed or had unknown 'role' for SuperAdmin subject {SubjectId}.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (!activeElement.GetBoolean()
            || !string.Equals(roleElement.GetString(), IdentityClosedRoles.SuperAdmin, StringComparison.Ordinal))
        {
            return IdentityAccountVerificationOutcome.Forbidden;
        }

        return IdentityAccountVerificationOutcome.Verified;
    }
}
