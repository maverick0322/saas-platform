using System.Security.Cryptography;
using System.Text;

namespace Saas.Platform.Api.Features.Tenants.GetTenantAccessState;

/// <summary>
/// Configuración explícita del servidor gRPC interno Platform–Identity (<c>TenantAccessService</c>).
/// Permanece deshabilitado por defecto hasta que exista configuración segura explícita.
/// </summary>
public sealed class InternalGrpcOptions
{
    public const string SectionName = "InternalGrpc";
    public const string HeaderName = "x-internal-service-key";
    public const int MinimumServiceKeyByteLength = 16;
    public const int DefaultHttpPort = 8080;
    public const int DefaultGrpcPort = 8092;

    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultGrpcPort;

    public string InternalServiceKey { get; set; } = string.Empty;

    public bool HasValidServiceKey() =>
        !string.IsNullOrWhiteSpace(InternalServiceKey)
        && Encoding.UTF8.GetByteCount(InternalServiceKey) >= MinimumServiceKeyByteLength;

    public void ValidateWhenEnabled(int httpPort)
    {
        if (!Enabled)
        {
            return;
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"InternalGrpc:Port must be between 1 and 65535 (received {Port}).");
        }

        if (Port == httpPort)
        {
            throw new InvalidOperationException(
                $"InternalGrpc:Port ({Port}) must be separated from HTTP port ({httpPort}).");
        }

        if (!HasValidServiceKey())
        {
            throw new InvalidOperationException(
                $"InternalGrpc:InternalServiceKey must be configured with at least {MinimumServiceKeyByteLength} UTF-8 bytes when InternalGrpc:Enabled is true.");
        }
    }

    public bool MatchesServiceKey(string? presentedKey)
    {
        if (!HasValidServiceKey() || string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(InternalServiceKey));
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}
