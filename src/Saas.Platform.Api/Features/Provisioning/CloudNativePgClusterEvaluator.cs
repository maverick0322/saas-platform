namespace Saas.Platform.Api.Features.Provisioning;

internal enum CloudNativePgClusterEvaluationKind
{
    Ready = 0,
    Reconciling = 1,
    TerminalConflict = 2
}

internal sealed record CloudNativePgClusterEvaluation(
    CloudNativePgClusterEvaluationKind Kind,
    string? TechnicalReference = null,
    string? ErrorCode = null,
    string? ErrorSummary = null);

/// <summary>
/// Evalúa compatibilidad de propiedad/especificación y disponibilidad estricta del <c>Cluster</c> CloudNativePG.
/// </summary>
internal static class CloudNativePgClusterEvaluator
{
    private const string HealthyClusterPhase = "Cluster in healthy state";

    private static readonly HashSet<string> UnrecoverablePhases = new(StringComparer.Ordinal)
    {
        "Cluster is unrecoverable and needs manual intervention",
        "Cluster has incomplete or invalid image catalog",
        "Cluster cannot proceed to reconciliation due to an unknown plugin being required",
        "Cluster cannot execute instance online upgrade due to missing architecture binary"
    };

    public static CloudNativePgClusterEvaluation Evaluate(
        CloudNativePgClusterSnapshot snapshot,
        ProvisioningStageContext context,
        CloudNativePgOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var expectedName = CloudNativePgClusterSpec.BuildClusterName(context.TenantId);
        if (!string.Equals(snapshot.Name, expectedName, StringComparison.Ordinal)
            || !string.Equals(snapshot.Namespace, options.Namespace, StringComparison.Ordinal))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_IDENTITY_MISMATCH",
                ErrorSummary: "Observed Kubernetes cluster does not match the deterministic tenant cluster identity.");
        }

        if (!snapshot.Labels.TryGetValue(CloudNativePgClusterSpec.ManagedByLabelKey, out var managedBy)
            || !string.Equals(managedBy, CloudNativePgClusterSpec.ManagedByLabelValue, StringComparison.Ordinal))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_OWNERSHIP_CONFLICT",
                ErrorSummary: "Existing cluster is not owned by saas-platform and cannot be adopted.");
        }

        var expectedTenantId = context.TenantId.ToString("D");
        if (!snapshot.Labels.TryGetValue(CloudNativePgClusterSpec.TenantIdLabelKey, out var tenantLabel)
            || !string.Equals(tenantLabel, expectedTenantId, StringComparison.Ordinal))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_OWNERSHIP_CONFLICT",
                ErrorSummary: "Existing cluster belongs to a different tenant identifier and cannot be adopted.");
        }

        var expectedOperationId = context.OperationId.ToString("D");
        if (!snapshot.Labels.TryGetValue(CloudNativePgClusterSpec.OperationIdLabelKey, out var operationLabel)
            || !string.Equals(operationLabel, expectedOperationId, StringComparison.Ordinal))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_OWNERSHIP_CONFLICT",
                ErrorSummary: "Existing cluster is missing or has a mismatched provisioning operation identity and cannot be adopted.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.DeletionTimestamp))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_DELETING",
                ErrorSummary: "Target CloudNativePG cluster is marked for deletion and cannot be used.");
        }

        if (!IsSpecCompatible(snapshot, options))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_SPEC_MISMATCH",
                ErrorSummary: "Existing CloudNativePG cluster specification does not match the approved tenant profile.");
        }

        if (options.ServiceDatabases.Enabled && !HasRequiredServicePgHbaRules(snapshot.PgHbaRules))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_PGHBA_NOT_CONFIGURED",
                ErrorSummary: "Cluster specification does not enforce the required TLS-only pg_hba rules for service database owners.");
        }

        if (!CloudNativePgClusterSpec.TryBuildTechnicalReference(
                snapshot.Namespace,
                snapshot.Name,
                snapshot.Uid,
                out var technicalReference))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "INVALID_TECHNICAL_REFERENCE",
                ErrorSummary: "CloudNativePG cluster metadata UID is missing or invalid for durable evidence.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Phase) && IsUnrecoverablePhase(snapshot.Phase))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_UNRECOVERABLE",
                ErrorSummary: "CloudNativePG cluster entered an unrecoverable operator phase and requires manual intervention.");
        }

        if (IsStrictlyReady(snapshot, options))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.Ready,
                TechnicalReference: technicalReference);
        }

        return new CloudNativePgClusterEvaluation(CloudNativePgClusterEvaluationKind.Reconciling);
    }

    public static CloudNativePgClusterEvaluation EvaluateForServiceDatabases(
        CloudNativePgClusterSnapshot snapshot,
        ProvisioningStageContext context,
        CloudNativePgOptions options,
        string expectedUid)
    {
        var baseEvaluation = Evaluate(snapshot, context, options);
        if (baseEvaluation.Kind != CloudNativePgClusterEvaluationKind.Ready)
        {
            return baseEvaluation;
        }

        if (!string.Equals(snapshot.Uid?.Trim(), expectedUid.Trim(), StringComparison.Ordinal))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_IDENTITY_MISMATCH",
                ErrorSummary: "Observed Kubernetes cluster UID does not match the confirmed InfrastructureInstance reference.");
        }

        if (!HasRequiredServicePgHbaRules(snapshot.PgHbaRules))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_PGHBA_NOT_CONFIGURED",
                ErrorSummary: "Cluster specification does not enforce the required TLS-only pg_hba rules for service database owners.");
        }

        var expectedWriteService = $"{snapshot.Name}-rw";
        if (!string.IsNullOrWhiteSpace(snapshot.WriteService)
            && !string.Equals(snapshot.WriteService.Trim(), expectedWriteService, StringComparison.Ordinal))
        {
            return new CloudNativePgClusterEvaluation(
                CloudNativePgClusterEvaluationKind.TerminalConflict,
                ErrorCode: "CNPG_CLUSTER_IDENTITY_MISMATCH",
                ErrorSummary: "Observed Kubernetes cluster writeService does not match the deterministic tenant cluster.");
        }

        return baseEvaluation;
    }

    public static bool HasRequiredServicePgHbaRules(IReadOnlyList<string>? pgHbaRules)
    {
        if (pgHbaRules is null || pgHbaRules.Count < CloudNativePgClusterSpec.RequiredServicePgHbaRules.Count)
        {
            return false;
        }

        var allowIdentity = NormalizeHbaLine(CloudNativePgClusterSpec.RequiredServicePgHbaRules[0]);
        var allowClinical = NormalizeHbaLine(CloudNativePgClusterSpec.RequiredServicePgHbaRules[1]);
        var rejectOwners = NormalizeHbaLine(CloudNativePgClusterSpec.RequiredServicePgHbaRules[2]);

        var normalized = pgHbaRules.Select(NormalizeHbaLine).ToList();
        var allowIdentityIndex = normalized.IndexOf(allowIdentity);
        var allowClinicalIndex = normalized.IndexOf(allowClinical);
        var rejectIndex = normalized.IndexOf(rejectOwners);

        if (allowIdentityIndex < 0 || allowClinicalIndex < 0 || rejectIndex < 0)
        {
            return false;
        }

        if (allowIdentityIndex > rejectIndex || allowClinicalIndex > rejectIndex)
        {
            return false;
        }

        // Ninguna otra regla previa al reject puede conceder acceso amplio a identity_owner, clinical_owner o all.
        for (var i = 0; i < rejectIndex; i++)
        {
            if (i == allowIdentityIndex || i == allowClinicalIndex)
            {
                continue;
            }

            var tokens = normalized[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 4 && !tokens[^1].Equals("reject", StringComparison.OrdinalIgnoreCase))
            {
                var users = tokens[3];
                if (users.Contains("identity_owner", StringComparison.Ordinal)
                    || users.Contains("clinical_owner", StringComparison.Ordinal)
                    || string.Equals(users, "all", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string NormalizeHbaLine(string line)
        => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsSpecCompatible(CloudNativePgClusterSnapshot snapshot, CloudNativePgOptions options)
    {
        if (snapshot.Instances != options.Instances)
        {
            return false;
        }

        if (!string.Equals(snapshot.ImageName, options.ImageName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(snapshot.StorageClass, options.StorageClass, StringComparison.Ordinal))
        {
            return false;
        }

        if (!KubernetesResourceQuantity.AreEqual(snapshot.StorageSize, options.StorageSize))
        {
            return false;
        }

        if (!KubernetesResourceQuantity.AreEqual(snapshot.CpuRequest, options.CpuRequest)
            || !KubernetesResourceQuantity.AreEqual(snapshot.CpuLimit, options.CpuLimit)
            || !KubernetesResourceQuantity.AreEqual(snapshot.MemoryRequest, options.MemoryRequest)
            || !KubernetesResourceQuantity.AreEqual(snapshot.MemoryLimit, options.MemoryLimit))
        {
            return false;
        }

        return true;
    }

    private static bool IsUnrecoverablePhase(string phase)
        => UnrecoverablePhases.Contains(phase)
            || phase.Contains("unrecoverable", StringComparison.OrdinalIgnoreCase);

    private static bool IsStrictlyReady(CloudNativePgClusterSnapshot snapshot, CloudNativePgOptions options)
    {
        if (snapshot.StatusInstances != options.Instances || snapshot.ReadyInstances != options.Instances)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshot.CurrentPrimary))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.TargetPrimary)
            && !string.Equals(snapshot.TargetPrimary, snapshot.CurrentPrimary, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(snapshot.Phase, HealthyClusterPhase, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(snapshot.ReadyConditionStatus, "True", StringComparison.Ordinal))
        {
            return false;
        }

        if (snapshot.Generation.HasValue
            && snapshot.ReadyConditionObservedGeneration.HasValue
            && snapshot.ReadyConditionObservedGeneration.Value < snapshot.Generation.Value)
        {
            return false;
        }

        return true;
    }
}
