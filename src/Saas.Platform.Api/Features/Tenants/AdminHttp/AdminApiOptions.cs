using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

public sealed class AdminApiOptions
{
    public const string SectionName = "AdminApi";

    public bool Enabled { get; set; }
}

public sealed class JwtValidationOptions
{
    public const string SectionName = "Jwt";
    public const string DefaultIssuer = "saas-identity";
    public const string DefaultAudience = "vital-saas";
    public const int DefaultClockSkewSeconds = 30;
    public const int MinimumRsaKeySizeBits = 2048;

    private static readonly string[] ForbiddenConfigKeys =
    [
        "Jwt:PrivateKeyPem",
        "Jwt:PrivateKeyPemPath",
        "Jwt:RefreshTokenHmacKey",
        "Jwt:SigningKey"
    ];

    public string Issuer { get; set; } = DefaultIssuer;

    public string Audience { get; set; } = DefaultAudience;

    public string PublicKeyPem { get; set; } = string.Empty;

    public string PublicKeyPemPath { get; set; } = string.Empty;

    public RsaSecurityKey ValidateAndCreateSigningKey(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var forbiddenKey in ForbiddenConfigKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[forbiddenKey]))
            {
                throw new InvalidOperationException(
                    $"Configuration key '{forbiddenKey}' is forbidden in Saas.Platform.Api. Platform must only receive an RSA public key.");
            }
        }

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new InvalidOperationException("Jwt:Issuer must be configured when AdminApi:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("Jwt:Audience must be configured when AdminApi:Enabled is true.");
        }

        var pemContent = LoadPemContent();
        if (pemContent.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Jwt public key configuration contains private key material, which is forbidden in Saas.Platform.Api.");
        }

        if (!pemContent.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
            || !pemContent.Contains("-----END PUBLIC KEY-----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Jwt public key must be provided in SubjectPublicKeyInfo PEM format ('-----BEGIN PUBLIC KEY-----').");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pemContent);

            if (rsa.KeySize < MinimumRsaKeySizeBits)
            {
                throw new InvalidOperationException(
                    $"Jwt RSA public key size must be at least {MinimumRsaKeySizeBits} bits (received {rsa.KeySize} bits).");
            }

            return new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("Jwt public key PEM could not be imported as a valid RSA public key.", ex);
        }
    }

    private string LoadPemContent()
    {
        var hasInline = !string.IsNullOrWhiteSpace(PublicKeyPem);
        var hasPath = !string.IsNullOrWhiteSpace(PublicKeyPemPath);

        if (hasInline == hasPath)
        {
            throw new InvalidOperationException(
                "Configure exactly one of Jwt:PublicKeyPem or Jwt:PublicKeyPemPath when AdminApi:Enabled is true.");
        }

        if (hasPath)
        {
            var path = PublicKeyPemPath.Trim();
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Jwt:PublicKeyPemPath file '{path}' was not found.");
            }

            var fileContent = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(fileContent))
            {
                throw new InvalidOperationException($"Jwt:PublicKeyPemPath file '{path}' is empty.");
            }

            return fileContent;
        }

        return PublicKeyPem.Trim().Replace("\\n", "\n", StringComparison.Ordinal);
    }
}

public sealed class IdentityAuthOptions
{
    public const string SectionName = "IdentityAuth";
    public const int DefaultTimeoutSeconds = 3;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 30;

    public string BaseAddress { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public Uri ValidateAndGetBaseUri()
    {
        if (string.IsNullOrWhiteSpace(BaseAddress))
        {
            throw new InvalidOperationException("IdentityAuth:BaseAddress must be configured when AdminApi:Enabled is true.");
        }

        var trimmed = BaseAddress.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new InvalidOperationException(
                "IdentityAuth:BaseAddress must be a clean absolute HTTP or HTTPS URI without credentials, query, or fragment.");
        }

        if (TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"IdentityAuth:TimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");
        }

        var normalizedBase = trimmed.EndsWith('/') ? trimmed : $"{trimmed}/";
        return new Uri(normalizedBase, UriKind.Absolute);
    }
}
