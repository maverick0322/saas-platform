using System.Globalization;
using System.Text.RegularExpressions;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Configuración controlada del adaptador CloudNativePG para la etapa <c>InfrastructureInstance</c> (PLAT-05A).
/// Replica el perfil técnico verificado en DB-01C sin credenciales, roles SQL ni bases por servicio.
/// </summary>
public sealed class CloudNativePgOptions
{
    public const string SectionName = "CloudNativePg";

    // {namespace}(<=54) + "/" (1) + "pg-" (3) + uuidN(32) + "@" (1) + uid(36) = 127 <= 128
    public const int MaxNamespaceLength = 54;

    private static readonly Regex Dns1123LabelPattern =
        new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Dns1123SubdomainPattern =
        new("^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ImageReferencePattern =
        new("^[^\\s\\x00-\\x1F]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Namespace { get; set; } = "saas-tenant-databases";

    public string ImageName { get; set; } =
        "ghcr.io/cloudnative-pg/postgresql:17.12-system-trixie@sha256:0f34d3fb12f415713ec94c3f2aa8295c7975f7114a0a8d360f26c958253bd032";

    public int Instances { get; set; } = 1;

    public string StorageClass { get; set; } = "local-path";

    public string StorageSize { get; set; } = "2Gi";

    public string CpuRequest { get; set; } = "100m";

    public string CpuLimit { get; set; } = "750m";

    public string MemoryRequest { get; set; } = "256Mi";

    public string MemoryLimit { get; set; } = "768Mi";

    public int ApiCallTimeoutSeconds { get; set; } = 10;

    public int ReadinessTimeoutSeconds { get; set; } = 180;

    public int ReadinessPollIntervalSeconds { get; set; } = 3;

    public CloudNativePgServiceDatabasesOptions ServiceDatabases { get; set; } = new();

    public TimeSpan ApiCallTimeout => TimeSpan.FromSeconds(ApiCallTimeoutSeconds);

    public TimeSpan ReadinessTimeout => TimeSpan.FromSeconds(ReadinessTimeoutSeconds);

    public TimeSpan ReadinessPollInterval => TimeSpan.FromSeconds(ReadinessPollIntervalSeconds);

    public static bool IsValidDns1123Label(string? value, int maxLength = 63)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && Dns1123LabelPattern.IsMatch(value);

    public static bool IsValidDns1123Subdomain(string? value, int maxLength = 253)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && Dns1123SubdomainPattern.IsMatch(value);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Namespace)
            || Namespace.Length > MaxNamespaceLength
            || !Dns1123LabelPattern.IsMatch(Namespace))
        {
            throw new InvalidOperationException(
                $"CloudNativePg:Namespace must be a valid DNS-1123 label with at most {MaxNamespaceLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(ImageName)
            || ImageName.Length > 256
            || !ImageReferencePattern.IsMatch(ImageName))
        {
            throw new InvalidOperationException("CloudNativePg:ImageName must be a valid container image reference.");
        }

        var withoutDigest = ImageName.Split('@', 2)[0];
        var lastColon = withoutDigest.LastIndexOf(':');
        var lastSlash = withoutDigest.LastIndexOf('/');
        var hasExplicitTag = lastColon > lastSlash && lastColon < withoutDigest.Length - 1;
        var hasDigest = ImageName.Contains("@sha256:", StringComparison.Ordinal);

        if (!hasExplicitTag && !hasDigest)
        {
            throw new InvalidOperationException(
                "CloudNativePg:ImageName must pin an immutable digest or explicit non-latest version tag.");
        }

        if (hasExplicitTag && string.Equals(withoutDigest[(lastColon + 1)..], "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "CloudNativePg:ImageName must not use the ':latest' tag.");
        }

        if (Instances != 1)
        {
            throw new InvalidOperationException(
                "CloudNativePg:Instances must be 1 in PLAT-05A (single-primary profile).");
        }

        if (string.IsNullOrWhiteSpace(StorageClass)
            || !Dns1123SubdomainPattern.IsMatch(StorageClass))
        {
            throw new InvalidOperationException("CloudNativePg:StorageClass must be a valid Kubernetes storage class name.");
        }

        if (!KubernetesResourceQuantity.TryParsePositive(StorageSize, out _))
        {
            throw new InvalidOperationException("CloudNativePg:StorageSize must be a positive Kubernetes resource quantity.");
        }

        if (!KubernetesResourceQuantity.TryParsePositive(CpuRequest, out var cpuRequestValue)
            || !KubernetesResourceQuantity.TryParsePositive(CpuLimit, out var cpuLimitValue))
        {
            throw new InvalidOperationException("CloudNativePg:CpuRequest and CpuLimit must be positive Kubernetes resource quantities.");
        }

        if (cpuRequestValue > cpuLimitValue)
        {
            throw new InvalidOperationException("CloudNativePg:CpuRequest must not exceed CpuLimit.");
        }

        if (!KubernetesResourceQuantity.TryParsePositive(MemoryRequest, out var memoryRequestValue)
            || !KubernetesResourceQuantity.TryParsePositive(MemoryLimit, out var memoryLimitValue))
        {
            throw new InvalidOperationException("CloudNativePg:MemoryRequest and MemoryLimit must be positive Kubernetes resource quantities.");
        }

        if (memoryRequestValue > memoryLimitValue)
        {
            throw new InvalidOperationException("CloudNativePg:MemoryRequest must not exceed MemoryLimit.");
        }

        if (ApiCallTimeoutSeconds < 1 || ApiCallTimeoutSeconds > 60)
        {
            throw new InvalidOperationException("CloudNativePg:ApiCallTimeoutSeconds must be between 1 and 60.");
        }

        if (ReadinessTimeoutSeconds < 1 || ReadinessTimeoutSeconds > 600)
        {
            throw new InvalidOperationException("CloudNativePg:ReadinessTimeoutSeconds must be between 1 and 600.");
        }

        if (ReadinessPollIntervalSeconds < 1 || ReadinessPollIntervalSeconds > ReadinessTimeoutSeconds)
        {
            throw new InvalidOperationException(
                "CloudNativePg:ReadinessPollIntervalSeconds must be between 1 and CloudNativePg:ReadinessTimeoutSeconds.");
        }

        ServiceDatabases ??= new CloudNativePgServiceDatabasesOptions();
        ServiceDatabases.Validate(Namespace);
    }
}

public sealed class ServiceDistributionSecretOptions
{
    public string Namespace { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class CloudNativePgServiceDatabasesOptions
{
    public bool Enabled { get; set; }

    public string ServiceDnsSuffix { get; set; } = "svc";

    public int PostgreSqlPort { get; set; } = 5432;

    public int SqlConnectTimeoutSeconds { get; set; } = 5;

    public int SqlCommandTimeoutSeconds { get; set; } = 10;

    public int SecretDistributionMaxConcurrencyRetries { get; set; } = 5;

    public ServiceDistributionSecretOptions IdentityDistributionSecret { get; set; } = new();

    public ServiceDistributionSecretOptions ClinicalDistributionSecret { get; set; } = new();

    public TimeSpan SqlConnectTimeout => TimeSpan.FromSeconds(SqlConnectTimeoutSeconds);

    public TimeSpan SqlCommandTimeout => TimeSpan.FromSeconds(SqlCommandTimeoutSeconds);

    public void Validate(string clusterNamespace)
    {
        if (!CloudNativePgOptions.IsValidDns1123Subdomain(ServiceDnsSuffix))
        {
            throw new InvalidOperationException(
                "CloudNativePg:ServiceDatabases:ServiceDnsSuffix must be a valid DNS-1123 subdomain.");
        }

        if (PostgreSqlPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "CloudNativePg:ServiceDatabases:PostgreSqlPort must be between 1 and 65535.");
        }

        if (SqlConnectTimeoutSeconds is < 1 or > 30)
        {
            throw new InvalidOperationException(
                "CloudNativePg:ServiceDatabases:SqlConnectTimeoutSeconds must be between 1 and 30.");
        }

        if (SqlCommandTimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException(
                "CloudNativePg:ServiceDatabases:SqlCommandTimeoutSeconds must be between 1 and 60.");
        }

        if (SecretDistributionMaxConcurrencyRetries is < 1 or > 10)
        {
            throw new InvalidOperationException(
                "CloudNativePg:ServiceDatabases:SecretDistributionMaxConcurrencyRetries must be between 1 and 10.");
        }

        if (!Enabled)
        {
            return;
        }

        IdentityDistributionSecret ??= new ServiceDistributionSecretOptions();
        ClinicalDistributionSecret ??= new ServiceDistributionSecretOptions();

        ValidateDistributionSecret(IdentityDistributionSecret, "IdentityDistributionSecret", clusterNamespace);
        ValidateDistributionSecret(ClinicalDistributionSecret, "ClinicalDistributionSecret", clusterNamespace);

        if (string.Equals(IdentityDistributionSecret.Namespace, ClinicalDistributionSecret.Namespace, StringComparison.Ordinal)
            && string.Equals(IdentityDistributionSecret.Name, ClinicalDistributionSecret.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CloudNativePg:ServiceDatabases:IdentityDistributionSecret and ClinicalDistributionSecret must be distinct Kubernetes Secrets.");
        }
    }

    private static void ValidateDistributionSecret(
        ServiceDistributionSecretOptions secret,
        string optionName,
        string clusterNamespace)
    {
        if (!CloudNativePgOptions.IsValidDns1123Label(secret.Namespace))
        {
            throw new InvalidOperationException(
                $"CloudNativePg:ServiceDatabases:{optionName}:Namespace must be a valid Kubernetes namespace name.");
        }

        if (!CloudNativePgOptions.IsValidDns1123Subdomain(secret.Name))
        {
            throw new InvalidOperationException(
                $"CloudNativePg:ServiceDatabases:{optionName}:Name must be a valid Kubernetes Secret name.");
        }

        if (string.Equals(secret.Namespace, clusterNamespace, StringComparison.Ordinal)
            && secret.Name.StartsWith("pg-auth-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CloudNativePg:ServiceDatabases:{optionName} must be separated from CNPG DatabaseRole auth Secrets (pg-auth-*).");
        }
    }
}

internal static class KubernetesResourceQuantity
{
    public static bool TryParsePositive(string? raw, out decimal normalizedBaseValue)
    {
        normalizedBaseValue = 0m;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        var splitIndex = 0;
        while (splitIndex < trimmed.Length && (char.IsDigit(trimmed[splitIndex]) || trimmed[splitIndex] == '.'))
        {
            splitIndex++;
        }

        if (splitIndex == 0)
        {
            return false;
        }

        var numberPart = trimmed[..splitIndex];
        var suffixPart = trimmed[splitIndex..];

        if (!decimal.TryParse(numberPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var numericValue)
            || numericValue <= 0m)
        {
            return false;
        }

        decimal multiplier = suffixPart switch
        {
            "" => 1m,
            "m" => 0.001m,
            "k" => 1_000m,
            "M" => 1_000_000m,
            "G" => 1_000_000_000m,
            "T" => 1_000_000_000_000m,
            "P" => 1_000_000_000_000_000m,
            "Ki" => 1_024m,
            "Mi" => 1_048_576m,
            "Gi" => 1_073_741_824m,
            "Ti" => 1_099_511_627_776m,
            "Pi" => 1_125_899_906_842_624m,
            _ => -1m
        };

        if (multiplier <= 0m)
        {
            return false;
        }

        try
        {
            normalizedBaseValue = numericValue * multiplier;
            return normalizedBaseValue > 0m;
        }
        catch (OverflowException)
        {
            normalizedBaseValue = 0m;
            return false;
        }
    }

    public static bool AreEqual(string? left, string? right)
        => TryParsePositive(left, out var leftValue)
            && TryParsePositive(right, out var rightValue)
            && leftValue == rightValue;
}
