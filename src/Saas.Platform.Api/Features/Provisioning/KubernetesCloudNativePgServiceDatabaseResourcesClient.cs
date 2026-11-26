using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Implementación Kubernetes in-cluster para <c>DatabaseRole</c>, <c>Database</c> y <c>Secret</c> (PLAT-05B).
/// Nunca registra contraseñas, cadenas de conexión ni cuerpos HTTP crudos de respuestas de Kubernetes.
/// </summary>
internal sealed class KubernetesCloudNativePgServiceDatabaseResourcesClient(
    IKubernetesClientAccessor clientAccessor,
    CloudNativePgOptions options,
    ILogger<KubernetesCloudNativePgServiceDatabaseResourcesClient> logger)
    : ICloudNativePgServiceDatabaseResourcesClient
{
    public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> GetDatabaseRoleAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken)
        => ExecuteCustomGetAsync<CloudNativePgDatabaseRoleSnapshot>(
            @namespace,
            ServiceDatabaseTarget.DatabaseRolePlural,
            name,
            CloudNativePgDatabaseRoleSnapshot.TryParse,
            "DatabaseRole",
            cancellationToken);

    public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> CreateDatabaseRoleAsync(
        string @namespace,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken)
        => ExecuteCustomCreateAsync<CloudNativePgDatabaseRoleSnapshot>(
            @namespace,
            ServiceDatabaseTarget.DatabaseRolePlural,
            manifest,
            CloudNativePgDatabaseRoleSnapshot.TryParse,
            "DatabaseRole",
            cancellationToken);

    public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> ReplaceDatabaseRoleAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken)
        => ExecuteCustomReplaceAsync<CloudNativePgDatabaseRoleSnapshot>(
            @namespace,
            ServiceDatabaseTarget.DatabaseRolePlural,
            name,
            manifest,
            CloudNativePgDatabaseRoleSnapshot.TryParse,
            "DatabaseRole",
            cancellationToken);

    public Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> GetDatabaseAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken)
        => ExecuteCustomGetAsync<CloudNativePgDatabaseSnapshot>(
            @namespace,
            ServiceDatabaseTarget.DatabasePlural,
            name,
            CloudNativePgDatabaseSnapshot.TryParse,
            "Database",
            cancellationToken);

    public Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> CreateDatabaseAsync(
        string @namespace,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken)
        => ExecuteCustomCreateAsync<CloudNativePgDatabaseSnapshot>(
            @namespace,
            ServiceDatabaseTarget.DatabasePlural,
            manifest,
            CloudNativePgDatabaseSnapshot.TryParse,
            "Database",
            cancellationToken);

    public async Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> GetSecretAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var client = clientAccessor.GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        try
        {
            using var response = await client.CoreV1.ReadNamespacedSecretWithHttpMessagesAsync(
                name,
                @namespace,
                cancellationToken: timeoutCts.Token);

            var snapshot = ToSecretSnapshot(response.Body);
            return snapshot is null
                ? Terminal<KubernetesSecretSnapshot>("CNPG_API_REJECTED", "Kubernetes API returned an invalid Secret payload.")
                : new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.Found, Snapshot: snapshot);
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning("Kubernetes GET Secret {Namespace}/{SecretName} returned HTTP {StatusCode}.", @namespace, name, (int)statusCode);
            return MapGetHttpStatus<KubernetesSecretSnapshot>(statusCode, "Secret");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Kubernetes GET Secret {Namespace}/{SecretName} timed out.", @namespace, name);
            return Timeout<KubernetesSecretSnapshot>("CNPG_API_TIMEOUT_UNCERTAIN", "Timed out reading Kubernetes Secret.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Kubernetes GET Secret {Namespace}/{SecretName} failed ({StatusCode}).", @namespace, name, ex.StatusCode);
            return Transient<KubernetesSecretSnapshot>("CNPG_API_UNAVAILABLE", "Kubernetes API transport failed while reading Secret.");
        }
    }

    public async Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> CreateBasicAuthSecretAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, string> labels,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var client = clientAccessor.GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        var secret = new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = @namespace,
                Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal)
            },
            Type = ServiceDatabaseTarget.BasicAuthSecretType,
            Data = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ServiceDatabaseTarget.AuthSecretUsernameKey] = Encoding.UTF8.GetBytes(username),
                [ServiceDatabaseTarget.AuthSecretPasswordKey] = Encoding.UTF8.GetBytes(password)
            }
        };

        try
        {
            using var response = await client.CoreV1.CreateNamespacedSecretWithHttpMessagesAsync(
                secret,
                @namespace,
                cancellationToken: timeoutCts.Token);

            var snapshot = ToSecretSnapshot(response.Body);
            return snapshot is null
                ? Terminal<KubernetesSecretSnapshot>("CNPG_API_REJECTED", "Kubernetes API returned an invalid Secret payload after creation.")
                : new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.Created, Snapshot: snapshot);
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning("Kubernetes CREATE Secret {Namespace}/{SecretName} returned HTTP {StatusCode}.", @namespace, name, (int)statusCode);
            return MapWriteHttpStatus<KubernetesSecretSnapshot>(statusCode, "Secret");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Kubernetes CREATE Secret {Namespace}/{SecretName} timed out.", @namespace, name);
            return Timeout<KubernetesSecretSnapshot>("CNPG_CREATE_TIMEOUT_UNCERTAIN", "Timed out creating authentication Secret; verification required.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Kubernetes CREATE Secret {Namespace}/{SecretName} failed ({StatusCode}).", @namespace, name, ex.StatusCode);
            return Timeout<KubernetesSecretSnapshot>("CNPG_CREATE_TIMEOUT_UNCERTAIN", "Transport error occurred while creating authentication Secret; verification required.");
        }
    }

    public async Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> ReplaceSecretDataAsync(
        string @namespace,
        string name,
        KubernetesSecretSnapshot existingSnapshot,
        IReadOnlyDictionary<string, string> updatedData,
        CancellationToken cancellationToken)
    {
        var client = clientAccessor.GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        var encodedData = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in updatedData)
        {
            encodedData[entry.Key] = Encoding.UTF8.GetBytes(entry.Value);
        }

        var replacement = new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = new V1ObjectMeta
            {
                Name = existingSnapshot.Name,
                NamespaceProperty = existingSnapshot.Namespace,
                ResourceVersion = existingSnapshot.ResourceVersion,
                Labels = existingSnapshot.Labels.Count > 0
                    ? new Dictionary<string, string>(existingSnapshot.Labels, StringComparer.Ordinal)
                    : null,
                Annotations = existingSnapshot.Annotations.Count > 0
                    ? new Dictionary<string, string>(existingSnapshot.Annotations, StringComparer.Ordinal)
                    : null
            },
            Type = string.IsNullOrWhiteSpace(existingSnapshot.Type) ? "Opaque" : existingSnapshot.Type,
            Data = encodedData
        };

        try
        {
            using var response = await client.CoreV1.ReplaceNamespacedSecretWithHttpMessagesAsync(
                replacement,
                name,
                @namespace,
                cancellationToken: timeoutCts.Token);

            var snapshot = ToSecretSnapshot(response.Body);
            return snapshot is null
                ? Terminal<KubernetesSecretSnapshot>("CNPG_API_REJECTED", "Kubernetes API returned an invalid Secret payload after replacement.")
                : new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.Replaced, Snapshot: snapshot);
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning("Kubernetes REPLACE Secret {Namespace}/{SecretName} returned HTTP {StatusCode}.", @namespace, name, (int)statusCode);
            return MapWriteHttpStatus<KubernetesSecretSnapshot>(statusCode, "Secret");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Kubernetes REPLACE Secret {Namespace}/{SecretName} timed out.", @namespace, name);
            return Timeout<KubernetesSecretSnapshot>("CNPG_API_TIMEOUT_UNCERTAIN", "Timed out updating distribution Secret; verification required.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Kubernetes REPLACE Secret {Namespace}/{SecretName} failed ({StatusCode}).", @namespace, name, ex.StatusCode);
            return Timeout<KubernetesSecretSnapshot>("CNPG_API_TIMEOUT_UNCERTAIN", "Transport error occurred while updating distribution Secret; verification required.");
        }
    }

    private delegate bool TryParseSnapshot<TSnapshot>(JsonElement root, out TSnapshot? snapshot);

    private async Task<KubernetesResourceCallResult<TSnapshot>> ExecuteCustomGetAsync<TSnapshot>(
        string @namespace,
        string plural,
        string name,
        TryParseSnapshot<TSnapshot> parser,
        string resourceLabel,
        CancellationToken cancellationToken)
    {
        var client = clientAccessor.GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        try
        {
            using var response = await client.CustomObjects.GetNamespacedCustomObjectWithHttpMessagesAsync(
                CloudNativePgClusterSpec.CrdGroup,
                CloudNativePgClusterSpec.CrdVersion,
                @namespace,
                plural,
                name,
                cancellationToken: timeoutCts.Token);

            return TryConvert(response.Body, parser, out var snapshot) && snapshot is not null
                ? new KubernetesResourceCallResult<TSnapshot>(KubernetesResourceCallStatus.Found, Snapshot: snapshot)
                : Terminal<TSnapshot>("CNPG_INVALID_CLUSTER_PAYLOAD", $"Kubernetes API returned an invalid {resourceLabel} payload.");
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning("Kubernetes GET {ResourceKind} {Namespace}/{Name} returned HTTP {StatusCode}.", resourceLabel, @namespace, name, (int)statusCode);
            return MapGetHttpStatus<TSnapshot>(statusCode, resourceLabel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Kubernetes GET {ResourceKind} {Namespace}/{Name} timed out.", resourceLabel, @namespace, name);
            return Timeout<TSnapshot>("CNPG_API_TIMEOUT_UNCERTAIN", $"Timed out reading CloudNativePG {resourceLabel}.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Kubernetes GET {ResourceKind} {Namespace}/{Name} failed ({StatusCode}).", resourceLabel, @namespace, name, ex.StatusCode);
            return Transient<TSnapshot>("CNPG_API_UNAVAILABLE", $"Kubernetes API transport failed while querying {resourceLabel}.");
        }
    }

    private async Task<KubernetesResourceCallResult<TSnapshot>> ExecuteCustomCreateAsync<TSnapshot>(
        string @namespace,
        string plural,
        IReadOnlyDictionary<string, object?> manifest,
        TryParseSnapshot<TSnapshot> parser,
        string resourceLabel,
        CancellationToken cancellationToken)
    {
        var client = clientAccessor.GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        try
        {
            using var response = await client.CustomObjects.CreateNamespacedCustomObjectWithHttpMessagesAsync(
                manifest,
                CloudNativePgClusterSpec.CrdGroup,
                CloudNativePgClusterSpec.CrdVersion,
                @namespace,
                plural,
                cancellationToken: timeoutCts.Token);

            return TryConvert(response.Body, parser, out var snapshot) && snapshot is not null
                ? new KubernetesResourceCallResult<TSnapshot>(KubernetesResourceCallStatus.Created, Snapshot: snapshot)
                : Terminal<TSnapshot>("CNPG_INVALID_CLUSTER_PAYLOAD", $"Kubernetes API returned an invalid {resourceLabel} payload after creation.");
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning("Kubernetes CREATE {ResourceKind} in {Namespace} returned HTTP {StatusCode}.", resourceLabel, @namespace, (int)statusCode);
            return MapWriteHttpStatus<TSnapshot>(statusCode, resourceLabel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Kubernetes CREATE {ResourceKind} in {Namespace} timed out.", resourceLabel, @namespace);
            return Timeout<TSnapshot>("CNPG_CREATE_TIMEOUT_UNCERTAIN", $"Timed out creating CloudNativePG {resourceLabel}; verification required.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Kubernetes CREATE {ResourceKind} in {Namespace} failed ({StatusCode}).", resourceLabel, @namespace, ex.StatusCode);
            return Timeout<TSnapshot>("CNPG_CREATE_TIMEOUT_UNCERTAIN", $"Transport error occurred after submitting CloudNativePG {resourceLabel}; verification required.");
        }
    }

    private async Task<KubernetesResourceCallResult<TSnapshot>> ExecuteCustomReplaceAsync<TSnapshot>(
        string @namespace,
        string plural,
        string name,
        IReadOnlyDictionary<string, object?> manifest,
        TryParseSnapshot<TSnapshot> parser,
        string resourceLabel,
        CancellationToken cancellationToken)
    {
        var client = clientAccessor.GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        try
        {
            using var response = await client.CustomObjects.ReplaceNamespacedCustomObjectWithHttpMessagesAsync(
                manifest,
                CloudNativePgClusterSpec.CrdGroup,
                CloudNativePgClusterSpec.CrdVersion,
                @namespace,
                plural,
                name,
                cancellationToken: timeoutCts.Token);

            return TryConvert(response.Body, parser, out var snapshot) && snapshot is not null
                ? new KubernetesResourceCallResult<TSnapshot>(KubernetesResourceCallStatus.Replaced, Snapshot: snapshot)
                : Terminal<TSnapshot>("CNPG_INVALID_CLUSTER_PAYLOAD", $"Kubernetes API returned an invalid {resourceLabel} payload after update.");
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning("Kubernetes REPLACE {ResourceKind} {Namespace}/{Name} returned HTTP {StatusCode}.", resourceLabel, @namespace, name, (int)statusCode);
            return MapWriteHttpStatus<TSnapshot>(statusCode, resourceLabel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Kubernetes REPLACE {ResourceKind} {Namespace}/{Name} timed out.", resourceLabel, @namespace, name);
            return Timeout<TSnapshot>("CNPG_API_TIMEOUT_UNCERTAIN", $"Timed out updating CloudNativePG {resourceLabel}; verification required.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Kubernetes REPLACE {ResourceKind} {Namespace}/{Name} failed ({StatusCode}).", resourceLabel, @namespace, name, ex.StatusCode);
            return Timeout<TSnapshot>("CNPG_API_TIMEOUT_UNCERTAIN", $"Transport error occurred while updating CloudNativePG {resourceLabel}; verification required.");
        }
    }

    internal static KubernetesResourceCallResult<TSnapshot> MapGetHttpStatus<TSnapshot>(
        HttpStatusCode statusCode,
        string resourceLabel)
        => statusCode switch
        {
            HttpStatusCode.NotFound => new(KubernetesResourceCallStatus.NotFound),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                Timeout<TSnapshot>("CNPG_API_TIMEOUT_UNCERTAIN", $"Kubernetes API timed out while reading {resourceLabel}."),
            HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable =>
                Transient<TSnapshot>("CNPG_API_UNAVAILABLE", $"Kubernetes API is temporarily unavailable while reading {resourceLabel}."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Terminal<TSnapshot>("CNPG_RBAC_FORBIDDEN", $"Kubernetes API denied permission to read {resourceLabel}."),
            _ => Terminal<TSnapshot>("CNPG_API_REJECTED", $"Kubernetes API rejected {resourceLabel} lookup request.")
        };

    internal static KubernetesResourceCallResult<TSnapshot> MapWriteHttpStatus<TSnapshot>(
        HttpStatusCode statusCode,
        string resourceLabel)
        => statusCode switch
        {
            HttpStatusCode.Conflict => new(KubernetesResourceCallStatus.Conflict),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                Timeout<TSnapshot>("CNPG_CREATE_TIMEOUT_UNCERTAIN", $"Kubernetes API timed out writing {resourceLabel}; verification required."),
            HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable =>
                Transient<TSnapshot>("CNPG_API_UNAVAILABLE", $"Kubernetes API is temporarily unavailable writing {resourceLabel}."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Terminal<TSnapshot>("CNPG_RBAC_FORBIDDEN", $"Kubernetes API denied permission or quota for {resourceLabel}."),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                Terminal<TSnapshot>("CNPG_INVALID_MANIFEST", $"Kubernetes API rejected the {resourceLabel} manifest."),
            _ => Terminal<TSnapshot>("CNPG_API_REJECTED", $"Kubernetes API rejected {resourceLabel} write request.")
        };

    private static bool TryConvert<TSnapshot>(
        object? body,
        TryParseSnapshot<TSnapshot> parser,
        out TSnapshot? snapshot)
    {
        snapshot = default;
        if (body is JsonElement jsonElement)
        {
            return parser(jsonElement, out snapshot);
        }

        if (body is null)
        {
            return false;
        }

        try
        {
            var serialized = JsonSerializer.SerializeToElement(body);
            return parser(serialized, out snapshot);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static KubernetesSecretSnapshot? ToSecretSnapshot(V1Secret? secret)
    {
        if (secret?.Metadata is null
            || string.IsNullOrWhiteSpace(secret.Metadata.Name)
            || string.IsNullOrWhiteSpace(secret.Metadata.NamespaceProperty))
        {
            return null;
        }

        var decodedData = new Dictionary<string, string>(StringComparer.Ordinal);
        if (secret.Data is not null)
        {
            foreach (var entry in secret.Data)
            {
                if (entry.Value is not null)
                {
                    decodedData[entry.Key] = KubernetesSecretSnapshot.DecodeUtf8(entry.Value);
                }
            }
        }

        return new KubernetesSecretSnapshot(
            secret.Metadata.Name,
            secret.Metadata.NamespaceProperty,
            secret.Metadata.Uid,
            secret.Metadata.ResourceVersion,
            secret.Type,
            secret.Metadata.DeletionTimestamp?.ToString("O"),
            secret.Metadata.Labels is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(secret.Metadata.Labels, StringComparer.Ordinal),
            secret.Metadata.Annotations is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(secret.Metadata.Annotations, StringComparer.Ordinal),
            decodedData);
    }

    private static KubernetesResourceCallResult<TSnapshot> Timeout<TSnapshot>(string code, string summary)
        => new(KubernetesResourceCallStatus.TimeoutUncertain, ErrorCode: code, ErrorSummary: summary);

    private static KubernetesResourceCallResult<TSnapshot> Transient<TSnapshot>(string code, string summary)
        => new(KubernetesResourceCallStatus.TransientError, ErrorCode: code, ErrorSummary: summary);

    private static KubernetesResourceCallResult<TSnapshot> Terminal<TSnapshot>(string code, string summary)
        => new(KubernetesResourceCallStatus.TerminalError, ErrorCode: code, ErrorSummary: summary);
}
