namespace Saas.Platform.Api.Features.Provisioning;

internal enum KubernetesResourceCallStatus
{
    Found = 0,
    Created = 1,
    Replaced = 2,
    NotFound = 3,
    Conflict = 4,
    TimeoutUncertain = 5,
    TransientError = 6,
    TerminalError = 7
}

internal sealed record KubernetesResourceCallResult<TSnapshot>(
    KubernetesResourceCallStatus Status,
    TSnapshot? Snapshot = default,
    string? ErrorCode = null,
    string? ErrorSummary = null);

/// <summary>
/// Cliente Kubernetes acotado para gestionar <c>DatabaseRole</c>, <c>Database</c>,
/// <c>Secret</c> de autenticación CNPG y distribución controlada por servicio (PLAT-05B).
/// </summary>
internal interface ICloudNativePgServiceDatabaseResourcesClient
{
    Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> GetDatabaseRoleAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken);

    Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> CreateDatabaseRoleAsync(
        string @namespace,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken);

    Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> ReplaceDatabaseRoleAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken);

    Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> GetDatabaseAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken);

    Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> CreateDatabaseAsync(
        string @namespace,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken);

    Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> GetSecretAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken);

    Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> CreateBasicAuthSecretAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, string> labels,
        string username,
        string password,
        CancellationToken cancellationToken);

    Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> ReplaceSecretDataAsync(
        string @namespace,
        string name,
        KubernetesSecretSnapshot existingSnapshot,
        IReadOnlyDictionary<string, string> updatedData,
        CancellationToken cancellationToken);
}
