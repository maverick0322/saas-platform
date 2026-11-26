using System.Text.Json;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Proyección inmutable del recurso <c>Cluster</c> devuelto por la API de Kubernetes / CloudNativePG v1.30.2.
/// </summary>
internal sealed record CloudNativePgClusterSnapshot(
    string Name,
    string Namespace,
    string? Uid,
    long? Generation,
    string? DeletionTimestamp,
    IReadOnlyDictionary<string, string> Labels,
    int? Instances,
    string? ImageName,
    string? StorageClass,
    string? StorageSize,
    string? CpuRequest,
    string? CpuLimit,
    string? MemoryRequest,
    string? MemoryLimit,
    int? StatusInstances,
    int? ReadyInstances,
    string? CurrentPrimary,
    string? TargetPrimary,
    string? Phase,
    string? ReadyConditionStatus,
    string? ReadyConditionReason,
    long? ReadyConditionObservedGeneration,
    IReadOnlyList<string>? PgHbaRules = null,
    string? WriteService = null)
{
    public static bool TryParse(JsonElement root, out CloudNativePgClusterSnapshot? snapshot)
    {
        snapshot = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = GetStringOrNull(metadata, "name");
        var @namespace = GetStringOrNull(metadata, "namespace");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(@namespace))
        {
            return false;
        }

        var uid = GetStringOrNull(metadata, "uid");
        var generation = GetInt64OrNull(metadata, "generation");
        var deletionTimestamp = GetStringOrNull(metadata, "deletionTimestamp");
        var labels = ParseLabels(metadata);

        int? instances = null;
        string? imageName = null;
        string? storageClass = null;
        string? storageSize = null;
        string? cpuRequest = null;
        string? cpuLimit = null;
        string? memoryRequest = null;
        string? memoryLimit = null;
        IReadOnlyList<string>? pgHbaRules = null;

        if (root.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object)
        {
            instances = GetInt32OrNull(spec, "instances");
            imageName = GetStringOrNull(spec, "imageName");

            if (spec.TryGetProperty("postgresql", out var postgresql) && postgresql.ValueKind == JsonValueKind.Object
                && postgresql.TryGetProperty("pg_hba", out var hbaElement) && hbaElement.ValueKind == JsonValueKind.Array)
            {
                var parsedRules = new List<string>();
                foreach (var rule in hbaElement.EnumerateArray())
                {
                    if (rule.ValueKind == JsonValueKind.String)
                    {
                        var text = rule.GetString();
                        if (text is not null)
                        {
                            parsedRules.Add(text);
                        }
                    }
                }

                pgHbaRules = parsedRules;
            }

            if (spec.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.Object)
            {
                storageClass = GetStringOrNull(storage, "storageClass");
                storageSize = GetStringOrNull(storage, "size");
            }

            if (spec.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
            {
                if (resources.TryGetProperty("requests", out var requests) && requests.ValueKind == JsonValueKind.Object)
                {
                    cpuRequest = GetStringOrNull(requests, "cpu");
                    memoryRequest = GetStringOrNull(requests, "memory");
                }

                if (resources.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                {
                    cpuLimit = GetStringOrNull(limits, "cpu");
                    memoryLimit = GetStringOrNull(limits, "memory");
                }
            }
        }

        int? statusInstances = null;
        int? readyInstances = null;
        string? currentPrimary = null;
        string? targetPrimary = null;
        string? phase = null;
        string? readyConditionStatus = null;
        string? readyConditionReason = null;
        long? readyConditionObservedGeneration = null;
        string? writeService = null;

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            statusInstances = GetInt32OrNull(status, "instances");
            readyInstances = GetInt32OrNull(status, "readyInstances");
            currentPrimary = GetStringOrNull(status, "currentPrimary");
            targetPrimary = GetStringOrNull(status, "targetPrimary");
            phase = GetStringOrNull(status, "phase");
            writeService = GetStringOrNull(status, "writeService");

            if (status.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
            {
                foreach (var condition in conditions.EnumerateArray())
                {
                    if (condition.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!string.Equals(GetStringOrNull(condition, "type"), "Ready", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    readyConditionStatus = GetStringOrNull(condition, "status");
                    readyConditionReason = GetStringOrNull(condition, "reason");
                    readyConditionObservedGeneration = GetInt64OrNull(condition, "observedGeneration");
                    break;
                }
            }
        }

        snapshot = new CloudNativePgClusterSnapshot(
            name,
            @namespace,
            uid,
            generation,
            deletionTimestamp,
            labels,
            instances,
            imageName,
            storageClass,
            storageSize,
            cpuRequest,
            cpuLimit,
            memoryRequest,
            memoryLimit,
            statusInstances,
            readyInstances,
            currentPrimary,
            targetPrimary,
            phase,
            readyConditionStatus,
            readyConditionReason,
            readyConditionObservedGeneration,
            pgHbaRules,
            writeService);

        return true;
    }

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

    private static string? GetStringOrNull(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? GetInt32OrNull(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetInt64OrNull(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out var value)
            ? value
            : null;
}
