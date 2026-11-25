using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

/// <summary>
/// Cliente HTTP de solo autorización para confirmar vigencia y rol global en <c>GET /v1/auth/me</c> de Identity
/// antes de mutaciones administrativas sensibles (<c>D-HTTP-01</c>). Aplica plazo integral y tamaño máximo de respuesta,
/// fallando cerrado sin filtrar cuerpos crudos ni JWTs.
/// </summary>
internal sealed class IdentityMeAccountVerifier(
    HttpClient httpClient,
    ILogger<IdentityMeAccountVerifier> logger) : IIdentityAccountVerifier
{
    public const int MaxResponseBytes = 64 * 1024;
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

        using var integralTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan && httpClient.Timeout > TimeSpan.Zero)
        {
            integralTimeoutCts.CancelAfter(httpClient.Timeout);
        }

        var effectiveToken = integralTimeoutCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, MeRelativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, effectiveToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Identity /v1/auth/me verification exceeded integral timeout for SuperAdmin subject {SubjectId}. Failing closed.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning(
                ex,
                "Identity /v1/auth/me verification encountered a transport or response size failure for SuperAdmin subject {SubjectId}. Failing closed.",
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

            if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > MaxResponseBytes)
            {
                logger.LogWarning(
                    "Identity /v1/auth/me declared Content-Length {ContentLength} exceeding limit {MaxResponseBytes} for SuperAdmin subject {SubjectId}. Failing closed.",
                    declaredLength,
                    MaxResponseBytes,
                    expectedSubjectId);
                return IdentityAccountVerificationOutcome.Unavailable;
            }

            try
            {
                var payloadBytes = await ReadBoundedBodyAsync(response.Content, effectiveToken);
                if (payloadBytes is null)
                {
                    logger.LogWarning(
                        "Identity /v1/auth/me response body exceeded maximum limit {MaxResponseBytes} for SuperAdmin subject {SubjectId}. Failing closed.",
                        MaxResponseBytes,
                        expectedSubjectId);
                    return IdentityAccountVerificationOutcome.Unavailable;
                }

                using var document = JsonDocument.Parse(payloadBytes);
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
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    ex,
                    "Transport failure while reading Identity /v1/auth/me body for SuperAdmin subject {SubjectId}. Failing closed.",
                    expectedSubjectId);
                return IdentityAccountVerificationOutcome.Unavailable;
            }
            catch (JsonException)
            {
                logger.LogWarning(
                    "Identity /v1/auth/me returned malformed or incomplete JSON for SuperAdmin subject {SubjectId}. Failing closed.",
                    expectedSubjectId);
                return IdentityAccountVerificationOutcome.Unavailable;
            }
        }
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var totalRead = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            totalRead += read;
            if (totalRead > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
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

        var idCount = 0;
        var tenantIdCount = 0;
        var activeCount = 0;
        var roleCount = 0;
        JsonElement idElement = default;
        JsonElement tenantIdElement = default;
        JsonElement activeElement = default;
        JsonElement roleElement = default;

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(prop.Name, "id", StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Identity /v1/auth/me returned non-canonical 'id' property casing for SuperAdmin subject {SubjectId}.",
                        expectedSubjectId);
                    return IdentityAccountVerificationOutcome.Unavailable;
                }

                idCount++;
                idElement = prop.Value;
            }
            else if (string.Equals(prop.Name, "tenantId", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(prop.Name, "tenantId", StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Identity /v1/auth/me returned non-canonical 'tenantId' property casing for SuperAdmin subject {SubjectId}.",
                        expectedSubjectId);
                    return IdentityAccountVerificationOutcome.Unavailable;
                }

                tenantIdCount++;
                tenantIdElement = prop.Value;
            }
            else if (string.Equals(prop.Name, "active", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(prop.Name, "active", StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Identity /v1/auth/me returned non-canonical 'active' property casing for SuperAdmin subject {SubjectId}.",
                        expectedSubjectId);
                    return IdentityAccountVerificationOutcome.Unavailable;
                }

                activeCount++;
                activeElement = prop.Value;
            }
            else if (string.Equals(prop.Name, "role", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(prop.Name, "role", StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Identity /v1/auth/me returned non-canonical 'role' property casing for SuperAdmin subject {SubjectId}.",
                        expectedSubjectId);
                    return IdentityAccountVerificationOutcome.Unavailable;
                }

                roleCount++;
                roleElement = prop.Value;
            }
        }

        if (idCount != 1 || tenantIdCount != 1 || activeCount != 1 || roleCount != 1)
        {
            logger.LogWarning(
                "Identity /v1/auth/me payload had missing or duplicated security properties (id={IdCount}, tenantId={TenantIdCount}, active={ActiveCount}, role={RoleCount}) for SuperAdmin subject {SubjectId}.",
                idCount,
                tenantIdCount,
                activeCount,
                roleCount,
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var profileId)
            || profileId == Guid.Empty)
        {
            logger.LogWarning(
                "Identity /v1/auth/me payload had invalid 'id' for SuperAdmin subject {SubjectId}.",
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

        if (tenantIdElement.ValueKind != JsonValueKind.Null)
        {
            logger.LogWarning(
                "Identity /v1/auth/me returned non-null tenantId for global SuperAdmin subject {SubjectId}.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (activeElement.ValueKind != JsonValueKind.True && activeElement.ValueKind != JsonValueKind.False)
        {
            logger.LogWarning(
                "Identity /v1/auth/me payload had non-boolean 'active' for SuperAdmin subject {SubjectId}.",
                expectedSubjectId);
            return IdentityAccountVerificationOutcome.Unavailable;
        }

        if (roleElement.ValueKind != JsonValueKind.String
            || !IdentityClosedRoles.IsKnown(roleElement.GetString()))
        {
            logger.LogWarning(
                "Identity /v1/auth/me payload had unknown 'role' for SuperAdmin subject {SubjectId}.",
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
