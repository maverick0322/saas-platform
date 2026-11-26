using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Constructor determinístico del recurso <c>postgresql.cnpg.io/v1 Cluster</c> para un tenant (PLAT-05A).
/// No incluye credenciales, roles SQL ni bases por servicio (diferidos a PLAT-05B).
/// </summary>
internal static class CloudNativePgClusterSpec
{
    public const string CrdGroup = "postgresql.cnpg.io";
    public const string CrdVersion = "v1";
    public const string CrdPlural = "clusters";
    public const string CrdKind = "Cluster";
    public const string ApiVersion = $"{CrdGroup}/{CrdVersion}";

    public const string ManagedByLabelKey = "app.kubernetes.io/managed-by";
    public const string ManagedByLabelValue = "saas-platform";
    public const string ComponentLabelKey = "app.kubernetes.io/component";
    public const string ComponentLabelValue = "tenant-postgres";
    public const string TenantIdLabelKey = "vital.saas/tenant-id";
    public const string OperationIdLabelKey = "vital.saas/provisioning-operation-id";

    public static readonly IReadOnlyList<string> RequiredServicePgHbaRules =
    [
        "hostssl identity identity_owner all scram-sha-256",
        "hostssl clinical clinical_owner all scram-sha-256",
        "host all identity_owner,clinical_owner all reject"
    ];

    public static string BuildClusterName(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));
        }

        // 3 + 32 = 35 chars, cumpliendo el límite <= 50 chars del webhook de CloudNativePG v1.30.2.
        return $"pg-{tenantId:N}";
    }

    public static bool TryBuildTechnicalReference(
        string? @namespace,
        string? clusterName,
        string? uid,
        out string technicalReference)
    {
        technicalReference = string.Empty;

        if (string.IsNullOrWhiteSpace(@namespace)
            || string.IsNullOrWhiteSpace(clusterName)
            || string.IsNullOrWhiteSpace(uid))
        {
            return false;
        }

        var candidate = $"{@namespace.Trim()}/{clusterName.Trim()}@{uid.Trim()}";
        if (candidate.Length > TenantProvisioningOperation.TechnicalReferenceMaxLength)
        {
            return false;
        }

        try
        {
            var sanitized = TenantProvisioningOperation.SanitizeTechnicalReference(candidate);
            if (!string.Equals(sanitized, candidate, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        technicalReference = candidate;
        return true;
    }

    public static bool TryParseTechnicalReference(
        string? technicalReference,
        out string @namespace,
        out string clusterName,
        out string uid)
    {
        @namespace = string.Empty;
        clusterName = string.Empty;
        uid = string.Empty;

        if (string.IsNullOrWhiteSpace(technicalReference))
        {
            return false;
        }

        var slashIndex = technicalReference.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex <= 0)
        {
            return false;
        }

        var atIndex = technicalReference.IndexOf('@', slashIndex + 1);
        if (atIndex <= slashIndex + 1 || atIndex >= technicalReference.Length - 1)
        {
            return false;
        }

        var parsedNs = technicalReference[..slashIndex];
        var parsedName = technicalReference[(slashIndex + 1)..atIndex];
        var parsedUid = technicalReference[(atIndex + 1)..];

        if (!TryBuildTechnicalReference(parsedNs, parsedName, parsedUid, out var rebuilt)
            || !string.Equals(rebuilt, technicalReference, StringComparison.Ordinal))
        {
            return false;
        }

        @namespace = parsedNs;
        clusterName = parsedName;
        uid = parsedUid;
        return true;
    }

    public static IReadOnlyDictionary<string, object?> BuildClusterManifest(
        ProvisioningStageContext context,
        CloudNativePgOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var clusterName = BuildClusterName(context.TenantId);
        var spec = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["instances"] = options.Instances,
            ["imageName"] = options.ImageName,
            ["storage"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["storageClass"] = options.StorageClass,
                ["size"] = options.StorageSize
            },
            ["resources"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["requests"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cpu"] = options.CpuRequest,
                    ["memory"] = options.MemoryRequest
                },
                ["limits"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cpu"] = options.CpuLimit,
                    ["memory"] = options.MemoryLimit
                }
            }
        };

        if (options.ServiceDatabases.Enabled)
        {
            spec["postgresql"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pg_hba"] = RequiredServicePgHbaRules
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["apiVersion"] = ApiVersion,
            ["kind"] = CrdKind,
            ["metadata"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = clusterName,
                ["namespace"] = options.Namespace,
                ["labels"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ManagedByLabelKey] = ManagedByLabelValue,
                    [ComponentLabelKey] = ComponentLabelValue,
                    [TenantIdLabelKey] = context.TenantId.ToString("D"),
                    [OperationIdLabelKey] = context.OperationId.ToString("D")
                }
            },
            ["spec"] = spec
        };
    }
}
