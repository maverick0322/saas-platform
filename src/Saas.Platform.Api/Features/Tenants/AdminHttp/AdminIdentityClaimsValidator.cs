using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

internal static class AdminAuthPolicies
{
    public const string SuperAdmin = "PlatformSuperAdmin";
}

internal static class IdentityClosedRoles
{
    public const string SuperAdmin = "SUPER_ADMIN";
    public const string AdminClinica = "ADMIN_CLINICA";
    public const string Especialista = "ESPECIALISTA";
    public const string Auxiliar = "AUXILIAR";

    public static bool IsKnown(string? role) => role switch
    {
        SuperAdmin or AdminClinica or Especialista or Auxiliar => true,
        _ => false
    };
}

internal static class AdminIdentityClaimsValidator
{
    public static void Validate(TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryGetAlgorithmAndEncodedPayload(context.SecurityToken, out var algorithm, out var encodedPayload)
            || !string.Equals(algorithm, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
        {
            context.Fail("JWT signature algorithm must be RS256.");
            return;
        }

        var principal = context.Principal;
        if (principal is null)
        {
            context.Fail("Missing authenticated principal.");
            return;
        }

        if (!TryValidatePayloadJson(encodedPayload, out var payloadSub, out var payloadRole, out var payloadTenantId, out var failureReason))
        {
            context.Fail(failureReason);
            return;
        }

        var subjectClaims = principal.FindAll("sub").ToList();
        if (subjectClaims.Count != 1
            || !Guid.TryParse(subjectClaims[0].Value, out var principalSub)
            || principalSub == Guid.Empty
            || principalSub != payloadSub)
        {
            context.Fail("JWT 'sub' claim must appear once as a non-empty UUID.");
            return;
        }

        var roleClaims = principal.FindAll("role").ToList();
        if (roleClaims.Count != 1
            || !string.Equals(roleClaims[0].Value, payloadRole, StringComparison.Ordinal))
        {
            context.Fail("JWT 'role' claim must appear once and match a valid Identity role.");
            return;
        }

        var tenantClaims = principal.FindAll("tenant_id").ToList();
        if (string.Equals(payloadRole, IdentityClosedRoles.SuperAdmin, StringComparison.Ordinal))
        {
            if (tenantClaims.Count > 0 || payloadTenantId is not null)
            {
                context.Fail("SUPER_ADMIN tokens must not contain a tenant_id claim.");
            }

            return;
        }

        if (tenantClaims.Count != 1
            || payloadTenantId is null
            || !Guid.TryParse(tenantClaims[0].Value, out var principalTenantId)
            || principalTenantId == Guid.Empty
            || principalTenantId != payloadTenantId.Value)
        {
            context.Fail("Clinic role tokens must contain a single non-empty UUID tenant_id claim.");
        }
    }

    public static bool TryGetSubjectId(ClaimsPrincipal principal, out Guid subjectId)
    {
        subjectId = Guid.Empty;
        var claims = principal.FindAll("sub").ToList();
        if (claims.Count != 1)
        {
            return false;
        }

        return Guid.TryParse(claims[0].Value, out subjectId) && subjectId != Guid.Empty;
    }

    private static bool TryGetAlgorithmAndEncodedPayload(
        SecurityToken securityToken,
        out string? algorithm,
        out string encodedPayload)
    {
        switch (securityToken)
        {
            case JsonWebToken jsonWebToken:
                algorithm = jsonWebToken.Alg;
                encodedPayload = jsonWebToken.EncodedPayload;
                return !string.IsNullOrEmpty(encodedPayload);
            case JwtSecurityToken jwtSecurityToken:
                algorithm = jwtSecurityToken.Header.Alg;
                encodedPayload = jwtSecurityToken.EncodedPayload;
                return !string.IsNullOrEmpty(encodedPayload);
            default:
                algorithm = null;
                encodedPayload = string.Empty;
                return false;
        }
    }

    private static bool TryValidatePayloadJson(
        string encodedPayload,
        out Guid subjectId,
        out string role,
        out Guid? tenantId,
        out string failureReason)
    {
        subjectId = Guid.Empty;
        role = string.Empty;
        tenantId = null;

        JsonDocument payloadDoc;
        try
        {
            var payloadBytes = Base64UrlEncoder.DecodeBytes(encodedPayload);
            payloadDoc = JsonDocument.Parse(payloadBytes);
        }
        catch (Exception)
        {
            failureReason = "JWT payload is malformed.";
            return false;
        }

        using (payloadDoc)
        {
            if (payloadDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureReason = "JWT payload must be a JSON object.";
                return false;
            }

            var subCount = 0;
            var roleCount = 0;
            var tenantPropertyCount = 0;
            string? rawSub = null;
            string? rawRole = null;
            JsonElement tenantElement = default;

            foreach (var prop in payloadDoc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "sub", StringComparison.Ordinal))
                {
                    subCount++;
                    if (prop.Value.ValueKind != JsonValueKind.String)
                    {
                        failureReason = "JWT 'sub' claim must be a string UUID.";
                        return false;
                    }

                    rawSub = prop.Value.GetString();
                }
                else if (string.Equals(prop.Name, "role", StringComparison.Ordinal))
                {
                    roleCount++;
                    if (prop.Value.ValueKind != JsonValueKind.String)
                    {
                        failureReason = "JWT 'role' claim must be a single string.";
                        return false;
                    }

                    rawRole = prop.Value.GetString();
                }
                else if (string.Equals(prop.Name, "tenant_id", StringComparison.Ordinal))
                {
                    tenantPropertyCount++;
                    tenantElement = prop.Value;
                }
            }

            if (subCount != 1 || !Guid.TryParse(rawSub, out subjectId) || subjectId == Guid.Empty)
            {
                failureReason = "JWT 'sub' claim must be present once as a non-empty UUID.";
                return false;
            }

            if (roleCount != 1 || !IdentityClosedRoles.IsKnown(rawRole))
            {
                failureReason = "JWT 'role' claim is missing, ambiguous, or unknown.";
                return false;
            }

            role = rawRole!;

            if (string.Equals(role, IdentityClosedRoles.SuperAdmin, StringComparison.Ordinal))
            {
                if (tenantPropertyCount > 0)
                {
                    failureReason = "SUPER_ADMIN tokens must not include the tenant_id claim.";
                    return false;
                }

                failureReason = string.Empty;
                return true;
            }

            if (tenantPropertyCount != 1 || tenantElement.ValueKind != JsonValueKind.String)
            {
                failureReason = "Clinic role tokens must include a single string UUID tenant_id claim.";
                return false;
            }

            if (!Guid.TryParse(tenantElement.GetString(), out var parsedTenantId) || parsedTenantId == Guid.Empty)
            {
                failureReason = "Clinic role tokens must include a valid non-empty UUID tenant_id claim.";
                return false;
            }

            tenantId = parsedTenantId;
            failureReason = string.Empty;
            return true;
        }
    }
}
