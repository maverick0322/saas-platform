using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Saas.Platform.Tests;

internal sealed class TestJwtTokenFactory : IDisposable
{
    private readonly RSA _rsa2048 = RSA.Create(2048);
    private readonly RSA _otherRsa2048 = RSA.Create(2048);

    public string PublicKeyPem => _rsa2048.ExportSubjectPublicKeyInfoPem();

    public string PrivateKeyPem => _rsa2048.ExportPkcs8PrivateKeyPem();

    public static string CreateSmallRsa1024PublicKeyPem()
    {
        using var smallRsa = RSA.Create(1024);
        return smallRsa.ExportSubjectPublicKeyInfoPem();
    }

    public string CreateSuperAdminToken(
        Guid? subjectId = null,
        string issuer = "saas-identity",
        string audience = "vital-saas",
        TimeSpan? lifetime = null,
        DateTimeOffset? nowUtc = null)
    {
        var effectiveNow = nowUtc ?? DateTimeOffset.UtcNow;
        var exp = effectiveNow + (lifetime ?? TimeSpan.FromMinutes(15));
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = (subjectId ?? Guid.NewGuid()).ToString("D"),
            ["email"] = "superadmin@vital-demo.test",
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["iat"] = effectiveNow.ToUnixTimeSeconds(),
            ["nbf"] = effectiveNow.ToUnixTimeSeconds(),
            ["exp"] = exp.ToUnixTimeSeconds(),
            ["iss"] = issuer,
            ["aud"] = audience,
            ["role"] = "SUPER_ADMIN",
            ["full_name"] = "SuperAdmin Sintético"
        };

        return SignWithRsa256(JsonSerializer.Serialize(payload), _rsa2048);
    }

    public string CreateClinicUserToken(
        string role,
        Guid tenantId,
        Guid? subjectId = null,
        bool includeTenantId = true)
    {
        var effectiveNow = DateTimeOffset.UtcNow;
        var exp = effectiveNow.AddMinutes(15);
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = (subjectId ?? Guid.NewGuid()).ToString("D"),
            ["email"] = "clinic.user@vital-demo.test",
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["iat"] = effectiveNow.ToUnixTimeSeconds(),
            ["nbf"] = effectiveNow.ToUnixTimeSeconds(),
            ["exp"] = exp.ToUnixTimeSeconds(),
            ["iss"] = "saas-identity",
            ["aud"] = "vital-saas",
            ["role"] = role,
            ["full_name"] = "Usuario Clínica Sintético"
        };

        if (includeTenantId)
        {
            payload["tenant_id"] = tenantId.ToString("D");
        }

        return SignWithRsa256(JsonSerializer.Serialize(payload), _rsa2048);
    }

    public string CreateRawPayloadRs256Token(string rawPayloadJson, bool useWrongSigningKey = false) =>
        SignWithRsa256(rawPayloadJson, useWrongSigningKey ? _otherRsa2048 : _rsa2048);

    public string CreateHs256Token(string secret = "SuperSecretSymmetricKeyForTestingAtLeast32Bytes!")
    {
        var now = DateTimeOffset.UtcNow;
        var headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var payloadJson = JsonSerializer.Serialize(new
        {
            sub = Guid.NewGuid().ToString("D"),
            email = "superadmin@vital-demo.test",
            jti = Guid.NewGuid().ToString("D"),
            iat = now.ToUnixTimeSeconds(),
            nbf = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(15).ToUnixTimeSeconds(),
            iss = "saas-identity",
            aud = "vital-saas",
            role = "SUPER_ADMIN",
            full_name = "HS256 Attempt"
        });

        var headerEncoded = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(headerJson));
        var payloadEncoded = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = Encoding.UTF8.GetBytes($"{headerEncoded}.{payloadEncoded}");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = hmac.ComputeHash(signingInput);
        return $"{headerEncoded}.{payloadEncoded}.{Base64UrlEncoder.Encode(signature)}";
    }

    public string CreateAlgNoneToken()
    {
        var now = DateTimeOffset.UtcNow;
        var headerJson = """{"alg":"none","typ":"JWT"}""";
        var payloadJson = JsonSerializer.Serialize(new
        {
            sub = Guid.NewGuid().ToString("D"),
            email = "superadmin@vital-demo.test",
            jti = Guid.NewGuid().ToString("D"),
            iat = now.ToUnixTimeSeconds(),
            nbf = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(15).ToUnixTimeSeconds(),
            iss = "saas-identity",
            aud = "vital-saas",
            role = "SUPER_ADMIN",
            full_name = "Alg None Attempt"
        });

        var headerEncoded = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(headerJson));
        var payloadEncoded = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payloadJson));
        return $"{headerEncoded}.{payloadEncoded}.";
    }

    private static string SignWithRsa256(string payloadJson, RSA rsa)
    {
        const string headerJson = """{"alg":"RS256","typ":"JWT"}""";
        var headerEncoded = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(headerJson));
        var payloadEncoded = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = Encoding.UTF8.GetBytes($"{headerEncoded}.{payloadEncoded}");
        var signature = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{headerEncoded}.{payloadEncoded}.{Base64UrlEncoder.Encode(signature)}";
    }

    public void Dispose()
    {
        _rsa2048.Dispose();
        _otherRsa2048.Dispose();
    }
}
