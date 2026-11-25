using System.Net;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Exceptions;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Cliente Kubernetes real para crear y consultar recursos <c>Cluster</c> de CloudNativePG
/// dentro de un único namespace usando credenciales InCluster (ServiceAccount).
/// </summary>
internal sealed class KubernetesCloudNativePgClusterClient(
    CloudNativePgOptions options,
    ILogger<KubernetesCloudNativePgClusterClient> logger)
    : ICloudNativePgClusterClient, IDisposable
{
    private readonly Lock _sync = new();
    private IKubernetes? _client;

    public void EnsureInitialized()
    {
        _ = GetOrCreateClient();
    }

    public async Task<CloudNativePgGetResult> GetClusterAsync(
        string @namespace,
        string clusterName,
        CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        try
        {
            using var response = await client.CustomObjects.GetNamespacedCustomObjectWithHttpMessagesAsync(
                CloudNativePgClusterSpec.CrdGroup,
                CloudNativePgClusterSpec.CrdVersion,
                @namespace,
                CloudNativePgClusterSpec.CrdPlural,
                clusterName,
                cancellationToken: timeoutCts.Token);

            if (!TryConvertBodyToSnapshot(response.Body, out var snapshot) || snapshot is null)
            {
                return CloudNativePgGetResult.Terminal(
                    "CNPG_INVALID_CLUSTER_PAYLOAD",
                    "Kubernetes API returned an invalid CloudNativePG cluster payload.");
            }

            return CloudNativePgGetResult.FoundCluster(snapshot);
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning(
                "Kubernetes GET cluster {Namespace}/{ClusterName} returned HTTP status {StatusCode}.",
                @namespace,
                clusterName,
                (int)statusCode);

            return MapGetHttpStatus(statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Kubernetes GET cluster {Namespace}/{ClusterName} timed out after {TimeoutSeconds}s.",
                @namespace,
                clusterName,
                options.ApiCallTimeoutSeconds);

            return CloudNativePgGetResult.Timeout(
                "CNPG_API_TIMEOUT_UNCERTAIN",
                "Timed out querying CloudNativePG cluster state from Kubernetes API.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                "Kubernetes GET cluster {Namespace}/{ClusterName} failed with transport error ({StatusCode}).",
                @namespace,
                clusterName,
                ex.StatusCode);

            return CloudNativePgGetResult.Transient(
                "CNPG_API_UNAVAILABLE",
                "Kubernetes API transport failed while querying CloudNativePG cluster state.");
        }
    }

    public async Task<CloudNativePgCreateResult> CreateClusterAsync(
        string @namespace,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.ApiCallTimeout);

        try
        {
            using var response = await client.CustomObjects.CreateNamespacedCustomObjectWithHttpMessagesAsync(
                manifest,
                CloudNativePgClusterSpec.CrdGroup,
                CloudNativePgClusterSpec.CrdVersion,
                @namespace,
                CloudNativePgClusterSpec.CrdPlural,
                cancellationToken: timeoutCts.Token);

            if (!TryConvertBodyToSnapshot(response.Body, out var snapshot) || snapshot is null)
            {
                return CloudNativePgCreateResult.Terminal(
                    "CNPG_INVALID_CLUSTER_PAYLOAD",
                    "Kubernetes API returned an invalid CloudNativePG cluster payload after creation.");
            }

            return CloudNativePgCreateResult.CreatedCluster(snapshot);
        }
        catch (HttpOperationException ex)
        {
            var statusCode = ex.Response?.StatusCode ?? HttpStatusCode.InternalServerError;
            logger.LogWarning(
                "Kubernetes CREATE cluster in {Namespace} returned HTTP status {StatusCode}.",
                @namespace,
                (int)statusCode);

            return MapCreateHttpStatus(statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Kubernetes CREATE cluster in {Namespace} timed out after {TimeoutSeconds}s; verification required.",
                @namespace,
                options.ApiCallTimeoutSeconds);

            return CloudNativePgCreateResult.Uncertain(
                "CNPG_CREATE_TIMEOUT_UNCERTAIN",
                "Timed out submitting CloudNativePG cluster creation; state must be verified before retrying.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                "Kubernetes CREATE cluster in {Namespace} failed with transport error ({StatusCode}); verification required.",
                @namespace,
                ex.StatusCode);

            return CloudNativePgCreateResult.Uncertain(
                "CNPG_CREATE_TIMEOUT_UNCERTAIN",
                "Transport error occurred after submitting CloudNativePG cluster creation; verification required.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    private IKubernetes GetOrCreateClient()
    {
        lock (_sync)
        {
            if (_client is not null)
            {
                return _client;
            }

            try
            {
                var config = KubernetesClientConfiguration.InClusterConfig();
                _client = new Kubernetes(config);
                return _client;
            }
            catch (KubeConfigException ex)
            {
                throw new InvalidOperationException(
                    "Kubernetes in-cluster ServiceAccount configuration is unavailable for CloudNativePG provisioning.",
                    ex);
            }
        }
    }

    internal static CloudNativePgGetResult MapGetHttpStatus(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.NotFound => CloudNativePgGetResult.Missing(),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                CloudNativePgGetResult.Timeout(
                    "CNPG_API_TIMEOUT_UNCERTAIN",
                    "Kubernetes API timed out while reading CloudNativePG cluster state."),
            HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable =>
                CloudNativePgGetResult.Transient(
                    "CNPG_API_UNAVAILABLE",
                    "Kubernetes API is temporarily unavailable while querying CloudNativePG cluster."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                CloudNativePgGetResult.Terminal(
                    "CNPG_RBAC_FORBIDDEN",
                    "Kubernetes API denied permission to read CloudNativePG cluster in target namespace."),
            _ => CloudNativePgGetResult.Terminal(
                "CNPG_API_REJECTED",
                "Kubernetes API rejected the CloudNativePG cluster lookup request.")
        };

    internal static CloudNativePgCreateResult MapCreateHttpStatus(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Conflict => CloudNativePgCreateResult.ConflictAlreadyExists(),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                CloudNativePgCreateResult.Uncertain(
                    "CNPG_CREATE_TIMEOUT_UNCERTAIN",
                    "Kubernetes API timed out during CloudNativePG cluster creation; verification required."),
            HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable =>
                CloudNativePgCreateResult.Transient(
                    "CNPG_API_UNAVAILABLE",
                    "Kubernetes API is temporarily unavailable to create the CloudNativePG cluster."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                CloudNativePgCreateResult.Terminal(
                    "CNPG_RBAC_FORBIDDEN",
                    "Kubernetes API denied permission or quota to create CloudNativePG cluster in target namespace."),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                CloudNativePgCreateResult.Terminal(
                    "CNPG_INVALID_MANIFEST",
                    "Kubernetes API rejected the CloudNativePG cluster manifest."),
            _ => CloudNativePgCreateResult.Terminal(
                "CNPG_API_REJECTED",
                "Kubernetes API rejected CloudNativePG cluster creation.")
        };

    private static bool TryConvertBodyToSnapshot(object? body, out CloudNativePgClusterSnapshot? snapshot)
    {
        snapshot = null;
        if (body is JsonElement jsonElement)
        {
            return CloudNativePgClusterSnapshot.TryParse(jsonElement, out snapshot);
        }

        if (body is null)
        {
            return false;
        }

        try
        {
            var serialized = JsonSerializer.SerializeToElement(body);
            return CloudNativePgClusterSnapshot.TryParse(serialized, out snapshot);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
