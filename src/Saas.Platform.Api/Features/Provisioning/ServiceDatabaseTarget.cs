using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Definición fija de los dos servicios obligatorios en PLAT-05B (<c>identity</c> y <c>clinical</c>),
/// sus nombres PostgreSQL/Kubernetes determinísticos, constructores de manifiestos CNPG v1.30.2 y
/// evaluadores estrictos de compatibilidad y generación observada.
/// </summary>
internal sealed record ServiceDatabaseTarget(
    string ServiceKey,
    string DatabaseName,
    string RoleName)
{
    public const string DatabaseRolePlural = "databaseroles";
    public const string DatabaseRoleKind = "DatabaseRole";
    public const string DatabasePlural = "databases";
    public const string DatabaseKind = "Database";

    public const string ServiceLabelKey = "vital.saas/service";
    public const string PurposeLabelKey = "vital.saas/purpose";
    public const string DistributionPurposeLabelValue = "tenant-db-connections";
    public const string CnpgReloadLabelKey = "cnpg.io/reload";
    public const string CnpgClusterLabelKey = "cnpg.io/cluster";
    public const string BasicAuthSecretType = "kubernetes.io/basic-auth";
    public const string OpaqueSecretType = "Opaque";
    public const string AuthSecretUsernameKey = "username";
    public const string AuthSecretPasswordKey = "password";

    public static readonly ServiceDatabaseTarget Identity = new("identity", "identity", "identity_owner");
    public static readonly ServiceDatabaseTarget Clinical = new("clinical", "clinical", "clinical_owner");

    public static readonly IReadOnlyList<ServiceDatabaseTarget> MandatoryTargets =
    [
        Identity,
        Clinical
    ];

    public string BuildRoleResourceName(Guid tenantId)
        => $"role-{tenantId:N}-{ServiceKey}";

    public string BuildDatabaseResourceName(Guid tenantId)
        => $"db-{tenantId:N}-{ServiceKey}";

    public string BuildAuthSecretName(Guid tenantId)
        => $"pg-auth-{tenantId:N}-{ServiceKey}";

    public static string BuildDistributionEntryKey(Guid tenantId)
        => $"{tenantId:D}.connection";

    public static ServiceDistributionSecretOptions ResolveDistributionSecret(
        ServiceDatabaseTarget target,
        CloudNativePgServiceDatabasesOptions options)
        => target.ServiceKey switch
        {
            "identity" => options.IdentityDistributionSecret,
            "clinical" => options.ClinicalDistributionSecret,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.ServiceKey, "Unsupported service target.")
        };

    public static IReadOnlyDictionary<string, string> BuildResourceLabels(
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        bool includeCnpgReload = false)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CloudNativePgClusterSpec.ManagedByLabelKey] = CloudNativePgClusterSpec.ManagedByLabelValue,
            [CloudNativePgClusterSpec.ComponentLabelKey] = CloudNativePgClusterSpec.ComponentLabelValue,
            [CloudNativePgClusterSpec.TenantIdLabelKey] = context.TenantId.ToString("D"),
            [CloudNativePgClusterSpec.OperationIdLabelKey] = context.OperationId.ToString("D"),
            [ServiceLabelKey] = target.ServiceKey
        };

        if (includeCnpgReload)
        {
            labels[CnpgReloadLabelKey] = "true";
        }

        return labels;
    }

    public static IReadOnlyDictionary<string, object?> BuildDatabaseRoleManifest(
        ProvisioningStageContext context,
        CloudNativePgOptions options,
        ServiceDatabaseTarget target,
        string clusterName,
        bool loginEnabled,
        string? passwordSecretName = null,
        string? resourceVersion = null)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = target.BuildRoleResourceName(context.TenantId),
            ["namespace"] = options.Namespace,
            ["labels"] = BuildResourceLabels(context, target)
        };

        if (!string.IsNullOrWhiteSpace(resourceVersion))
        {
            metadata["resourceVersion"] = resourceVersion;
        }

        var spec = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cluster"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = clusterName
            },
            ["name"] = target.RoleName,
            ["ensure"] = "present",
            ["databaseRoleReclaimPolicy"] = "retain",
            ["login"] = loginEnabled,
            ["superuser"] = false,
            ["createdb"] = false,
            ["createrole"] = false,
            ["replication"] = false,
            ["bypassrls"] = false
        };

        if (!string.IsNullOrWhiteSpace(passwordSecretName))
        {
            spec["passwordSecret"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = passwordSecretName
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["apiVersion"] = CloudNativePgClusterSpec.ApiVersion,
            ["kind"] = DatabaseRoleKind,
            ["metadata"] = metadata,
            ["spec"] = spec
        };
    }

    public static IReadOnlyDictionary<string, object?> BuildDatabaseManifest(
        ProvisioningStageContext context,
        CloudNativePgOptions options,
        ServiceDatabaseTarget target,
        string clusterName)
        => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["apiVersion"] = CloudNativePgClusterSpec.ApiVersion,
            ["kind"] = DatabaseKind,
            ["metadata"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = target.BuildDatabaseResourceName(context.TenantId),
                ["namespace"] = options.Namespace,
                ["labels"] = BuildResourceLabels(context, target)
            },
            ["spec"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cluster"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = clusterName
                },
                ["name"] = target.DatabaseName,
                ["owner"] = target.RoleName,
                ["ensure"] = "present",
                ["databaseReclaimPolicy"] = "retain"
            }
        };

    public static string GenerateSecurePassword()
    {
        Span<byte> randomBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(randomBytes);

        // URL-safe Base64 sin '=' ni caracteres conflictivos para SCRAM/DSN.
        return Convert.ToBase64String(randomBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string BuildServiceConnectionString(
        string writeHost,
        int port,
        ServiceDatabaseTarget target,
        string password)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = writeHost,
            Port = port,
            Database = target.DatabaseName,
            Username = target.RoleName,
            Password = password,
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }

    public static bool AreConnectionStringsEquivalent(string? existingRaw, string expectedRaw)
    {
        if (string.IsNullOrWhiteSpace(existingRaw))
        {
            return false;
        }

        try
        {
            var existing = new NpgsqlConnectionStringBuilder(existingRaw.Trim());
            var expected = new NpgsqlConnectionStringBuilder(expectedRaw);

            return string.Equals(existing.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
                && existing.Port == expected.Port
                && string.Equals(existing.Database, expected.Database, StringComparison.Ordinal)
                && string.Equals(existing.Username, expected.Username, StringComparison.Ordinal)
                && string.Equals(existing.Password, expected.Password, StringComparison.Ordinal)
                && existing.SslMode == expected.SslMode;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool HasValidOwnershipLabels(
        IReadOnlyDictionary<string, string> labels,
        ProvisioningStageContext context,
        ServiceDatabaseTarget target)
        => labels.TryGetValue(CloudNativePgClusterSpec.ManagedByLabelKey, out var managedBy)
            && string.Equals(managedBy, CloudNativePgClusterSpec.ManagedByLabelValue, StringComparison.Ordinal)
            && labels.TryGetValue(CloudNativePgClusterSpec.TenantIdLabelKey, out var tenantLabel)
            && string.Equals(tenantLabel, context.TenantId.ToString("D"), StringComparison.Ordinal)
            && labels.TryGetValue(CloudNativePgClusterSpec.OperationIdLabelKey, out var opLabel)
            && string.Equals(opLabel, context.OperationId.ToString("D"), StringComparison.Ordinal)
            && labels.TryGetValue(ServiceLabelKey, out var serviceLabel)
            && string.Equals(serviceLabel, target.ServiceKey, StringComparison.Ordinal);
}

internal enum ServiceResourceEvaluationKind
{
    Ready = 0,
    Reconciling = 1,
    TerminalConflict = 2
}

internal sealed record ServiceResourceEvaluation(
    ServiceResourceEvaluationKind Kind,
    string? ErrorCode = null,
    string? ErrorSummary = null);

internal sealed record CloudNativePgDatabaseRoleSnapshot(
    string Name,
    string Namespace,
    string? Uid,
    string? ResourceVersion,
    long? Generation,
    string? DeletionTimestamp,
    IReadOnlyDictionary<string, string> Labels,
    string? ClusterName,
    string? RoleName,
    string? Ensure,
    string? ReclaimPolicy,
    bool? Login,
    bool? Superuser,
    bool? Createdb,
    bool? Createrole,
    bool? Replication,
    bool? BypassRls,
    string? PasswordSecretName,
    bool? Applied,
    long? ObservedGeneration,
    string? SecretResourceVersion)
{
    public static bool TryParse(JsonElement root, out CloudNativePgDatabaseRoleSnapshot? snapshot)
    {
        snapshot = null;
        if (!KubernetesJsonParser.TryParseMetadata(root, out var name, out var @namespace, out var uid, out var resourceVersion, out var generation, out var deletionTimestamp, out var labels))
        {
            return false;
        }

        string? clusterName = null;
        string? roleName = null;
        string? ensure = null;
        string? reclaimPolicy = null;
        bool? login = null;
        bool? superuser = null;
        bool? createdb = null;
        bool? createrole = null;
        bool? replication = null;
        bool? bypassRls = null;
        string? passwordSecretName = null;

        if (root.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object)
        {
            if (spec.TryGetProperty("cluster", out var cluster) && cluster.ValueKind == JsonValueKind.Object)
            {
                clusterName = KubernetesJsonParser.GetStringOrNull(cluster, "name");
            }

            roleName = KubernetesJsonParser.GetStringOrNull(spec, "name");
            ensure = KubernetesJsonParser.GetStringOrNull(spec, "ensure");
            reclaimPolicy = KubernetesJsonParser.GetStringOrNull(spec, "databaseRoleReclaimPolicy");
            login = KubernetesJsonParser.GetBoolOrNull(spec, "login");
            superuser = KubernetesJsonParser.GetBoolOrNull(spec, "superuser");
            createdb = KubernetesJsonParser.GetBoolOrNull(spec, "createdb");
            createrole = KubernetesJsonParser.GetBoolOrNull(spec, "createrole");
            replication = KubernetesJsonParser.GetBoolOrNull(spec, "replication");
            bypassRls = KubernetesJsonParser.GetBoolOrNull(spec, "bypassrls");

            if (spec.TryGetProperty("passwordSecret", out var passwordSecret) && passwordSecret.ValueKind == JsonValueKind.Object)
            {
                passwordSecretName = KubernetesJsonParser.GetStringOrNull(passwordSecret, "name");
            }
        }

        bool? applied = null;
        long? observedGeneration = null;
        string? secretResourceVersion = null;
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            applied = KubernetesJsonParser.GetBoolOrNull(status, "applied");
            observedGeneration = KubernetesJsonParser.GetInt64OrNull(status, "observedGeneration");
            secretResourceVersion = KubernetesJsonParser.GetStringOrNull(status, "secretResourceVersion");
        }

        snapshot = new CloudNativePgDatabaseRoleSnapshot(
            name,
            @namespace,
            uid,
            resourceVersion,
            generation,
            deletionTimestamp,
            labels,
            clusterName,
            roleName,
            ensure,
            reclaimPolicy,
            login,
            superuser,
            createdb,
            createrole,
            replication,
            bypassRls,
            passwordSecretName,
            applied,
            observedGeneration,
            secretResourceVersion);

        return true;
    }

    public ServiceResourceEvaluation Evaluate(
        ProvisioningStageContext context,
        CloudNativePgOptions options,
        ServiceDatabaseTarget target,
        string clusterName,
        bool requireLoginReady,
        string? expectedPasswordSecretName = null)
    {
        if (!string.Equals(Name, target.BuildRoleResourceName(context.TenantId), StringComparison.Ordinal)
            || !string.Equals(Namespace, options.Namespace, StringComparison.Ordinal)
            || !ServiceDatabaseTarget.HasValidOwnershipLabels(Labels, context, target))
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_ROLE_CONFLICT",
                $"DatabaseRole for service '{target.ServiceKey}' does not match deterministic tenant ownership labels.");
        }

        if (!string.IsNullOrWhiteSpace(DeletionTimestamp))
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_ROLE_CONFLICT",
                $"DatabaseRole for service '{target.ServiceKey}' is marked for deletion.");
        }

        if (!string.Equals(ClusterName, clusterName, StringComparison.Ordinal)
            || !string.Equals(RoleName, target.RoleName, StringComparison.Ordinal)
            || !string.Equals(Ensure, "present", StringComparison.Ordinal)
            || !string.Equals(ReclaimPolicy, "retain", StringComparison.Ordinal)
            || Superuser != false
            || Createdb != false
            || Createrole != false
            || Replication != false
            || BypassRls != false)
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_ROLE_CONFLICT",
                $"DatabaseRole specification for service '{target.ServiceKey}' is incompatible with isolated non-admin ownership.");
        }

        if (Login == true && string.IsNullOrWhiteSpace(PasswordSecretName))
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_ROLE_CONFLICT",
                $"Active DatabaseRole for service '{target.ServiceKey}' is missing passwordSecret reference.");
        }

        if (!string.IsNullOrWhiteSpace(expectedPasswordSecretName)
            && !string.IsNullOrWhiteSpace(PasswordSecretName)
            && !string.Equals(PasswordSecretName, expectedPasswordSecretName, StringComparison.Ordinal))
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_ROLE_CONFLICT",
                $"DatabaseRole for service '{target.ServiceKey}' references an unexpected authentication Secret.");
        }

        if (requireLoginReady
            && (Login != true || !string.Equals(PasswordSecretName, expectedPasswordSecretName, StringComparison.Ordinal)))
        {
            return new ServiceResourceEvaluation(ServiceResourceEvaluationKind.Reconciling);
        }

        if (Applied == true
            && Generation.HasValue
            && ObservedGeneration.HasValue
            && ObservedGeneration.Value >= Generation.Value)
        {
            return new ServiceResourceEvaluation(ServiceResourceEvaluationKind.Ready);
        }

        return new ServiceResourceEvaluation(ServiceResourceEvaluationKind.Reconciling);
    }
}

internal sealed record CloudNativePgDatabaseSnapshot(
    string Name,
    string Namespace,
    string? Uid,
    string? ResourceVersion,
    long? Generation,
    string? DeletionTimestamp,
    IReadOnlyDictionary<string, string> Labels,
    string? ClusterName,
    string? DatabaseName,
    string? Owner,
    string? Ensure,
    string? ReclaimPolicy,
    bool? Applied,
    long? ObservedGeneration)
{
    public static bool TryParse(JsonElement root, out CloudNativePgDatabaseSnapshot? snapshot)
    {
        snapshot = null;
        if (!KubernetesJsonParser.TryParseMetadata(root, out var name, out var @namespace, out var uid, out var resourceVersion, out var generation, out var deletionTimestamp, out var labels))
        {
            return false;
        }

        string? clusterName = null;
        string? databaseName = null;
        string? owner = null;
        string? ensure = null;
        string? reclaimPolicy = null;

        if (root.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object)
        {
            if (spec.TryGetProperty("cluster", out var cluster) && cluster.ValueKind == JsonValueKind.Object)
            {
                clusterName = KubernetesJsonParser.GetStringOrNull(cluster, "name");
            }

            databaseName = KubernetesJsonParser.GetStringOrNull(spec, "name");
            owner = KubernetesJsonParser.GetStringOrNull(spec, "owner");
            ensure = KubernetesJsonParser.GetStringOrNull(spec, "ensure");
            reclaimPolicy = KubernetesJsonParser.GetStringOrNull(spec, "databaseReclaimPolicy");
        }

        bool? applied = null;
        long? observedGeneration = null;
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            applied = KubernetesJsonParser.GetBoolOrNull(status, "applied");
            observedGeneration = KubernetesJsonParser.GetInt64OrNull(status, "observedGeneration");
        }

        snapshot = new CloudNativePgDatabaseSnapshot(
            name,
            @namespace,
            uid,
            resourceVersion,
            generation,
            deletionTimestamp,
            labels,
            clusterName,
            databaseName,
            owner,
            ensure,
            reclaimPolicy,
            applied,
            observedGeneration);

        return true;
    }

    public ServiceResourceEvaluation Evaluate(
        ProvisioningStageContext context,
        CloudNativePgOptions options,
        ServiceDatabaseTarget target,
        string clusterName)
    {
        if (!string.Equals(Name, target.BuildDatabaseResourceName(context.TenantId), StringComparison.Ordinal)
            || !string.Equals(Namespace, options.Namespace, StringComparison.Ordinal)
            || !ServiceDatabaseTarget.HasValidOwnershipLabels(Labels, context, target))
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_DATABASE_CONFLICT",
                $"Database for service '{target.ServiceKey}' does not match deterministic tenant ownership labels.");
        }

        if (!string.IsNullOrWhiteSpace(DeletionTimestamp))
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_DATABASE_CONFLICT",
                $"Database for service '{target.ServiceKey}' is marked for deletion.");
        }

        if (!string.Equals(ClusterName, clusterName, StringComparison.Ordinal)
            || !string.Equals(DatabaseName, target.DatabaseName, StringComparison.Ordinal)
            || !string.Equals(Owner, target.RoleName, StringComparison.Ordinal)
            || !string.Equals(Ensure, "present", StringComparison.Ordinal)
            || !string.Equals(ReclaimPolicy, "retain", StringComparison.Ordinal))
        {
            return new ServiceResourceEvaluation(
                ServiceResourceEvaluationKind.TerminalConflict,
                "CNPG_DATABASE_CONFLICT",
                $"Database specification for service '{target.ServiceKey}' does not match isolated owner configuration.");
        }

        if (Applied == true
            && Generation.HasValue
            && ObservedGeneration.HasValue
            && ObservedGeneration.Value >= Generation.Value)
        {
            return new ServiceResourceEvaluation(ServiceResourceEvaluationKind.Ready);
        }

        return new ServiceResourceEvaluation(ServiceResourceEvaluationKind.Reconciling);
    }
}

internal sealed record KubernetesSecretSnapshot(
    string Name,
    string Namespace,
    string? Uid,
    string? ResourceVersion,
    string? Type,
    string? DeletionTimestamp,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Annotations,
    IReadOnlyDictionary<string, string> Data)
{
    public bool TryValidateAuthSecret(
        ProvisioningStageContext context,
        CloudNativePgOptions options,
        ServiceDatabaseTarget target,
        out string password)
    {
        password = string.Empty;

        if (!string.Equals(Name, target.BuildAuthSecretName(context.TenantId), StringComparison.Ordinal)
            || !string.Equals(Namespace, options.Namespace, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(DeletionTimestamp)
            || !string.Equals(Type, ServiceDatabaseTarget.BasicAuthSecretType, StringComparison.Ordinal)
            || !ServiceDatabaseTarget.HasValidOwnershipLabels(Labels, context, target))
        {
            return false;
        }

        if (!Data.TryGetValue(ServiceDatabaseTarget.AuthSecretUsernameKey, out var username)
            || !string.Equals(username, target.RoleName, StringComparison.Ordinal)
            || !Data.TryGetValue(ServiceDatabaseTarget.AuthSecretPasswordKey, out var existingPassword)
            || string.IsNullOrWhiteSpace(existingPassword))
        {
            return false;
        }

        password = existingPassword;
        return true;
    }

    public bool TryValidateDistributionSecret(ServiceDatabaseTarget target)
    {
        if (!string.IsNullOrWhiteSpace(DeletionTimestamp))
        {
            return false;
        }

        if (!string.Equals(Type, ServiceDatabaseTarget.OpaqueSecretType, StringComparison.Ordinal))
        {
            return false;
        }

        if (Labels.ContainsKey(ServiceDatabaseTarget.CnpgClusterLabelKey)
            || Labels.ContainsKey(ServiceDatabaseTarget.CnpgReloadLabelKey))
        {
            return false;
        }

        return Labels.TryGetValue(CloudNativePgClusterSpec.ManagedByLabelKey, out var managedBy)
            && string.Equals(managedBy, CloudNativePgClusterSpec.ManagedByLabelValue, StringComparison.Ordinal)
            && Labels.TryGetValue(ServiceDatabaseTarget.PurposeLabelKey, out var purpose)
            && string.Equals(purpose, ServiceDatabaseTarget.DistributionPurposeLabelValue, StringComparison.Ordinal)
            && Labels.TryGetValue(ServiceDatabaseTarget.ServiceLabelKey, out var service)
            && string.Equals(service, target.ServiceKey, StringComparison.Ordinal);
    }

    public static string DecodeUtf8(byte[] bytes)
        => Encoding.UTF8.GetString(bytes);
}

internal static class KubernetesJsonParser
{
    public static bool TryParseMetadata(
        JsonElement root,
        out string name,
        out string @namespace,
        out string? uid,
        out string? resourceVersion,
        out long? generation,
        out string? deletionTimestamp,
        out IReadOnlyDictionary<string, string> labels)
    {
        name = string.Empty;
        @namespace = string.Empty;
        uid = null;
        resourceVersion = null;
        generation = null;
        deletionTimestamp = null;
        labels = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var parsedName = GetStringOrNull(metadata, "name");
        var parsedNamespace = GetStringOrNull(metadata, "namespace");
        if (string.IsNullOrWhiteSpace(parsedName) || string.IsNullOrWhiteSpace(parsedNamespace))
        {
            return false;
        }

        name = parsedName;
        @namespace = parsedNamespace;
        uid = GetStringOrNull(metadata, "uid");
        resourceVersion = GetStringOrNull(metadata, "resourceVersion");
        generation = GetInt64OrNull(metadata, "generation");
        deletionTimestamp = GetStringOrNull(metadata, "deletionTimestamp");
        labels = ParseLabels(metadata);
        return true;
    }

    public static string? GetStringOrNull(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    public static bool? GetBoolOrNull(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var prop)
            && (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
            ? prop.GetBoolean()
            : null;

    public static long? GetInt64OrNull(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out var value)
            ? value
            : null;

    private static IReadOnlyDictionary<string, string> ParseLabels(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("labels", out var labelsElement) || labelsElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in labelsElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (value is not null)
                {
                    result[property.Name] = value;
                }
            }
        }

        return result;
    }
}
