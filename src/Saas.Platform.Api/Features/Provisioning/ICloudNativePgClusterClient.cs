namespace Saas.Platform.Api.Features.Provisioning;

internal enum CloudNativePgGetStatus
{
    Found = 0,
    NotFound = 1,
    TimeoutUncertain = 2,
    TransientError = 3,
    TerminalError = 4
}

internal sealed record CloudNativePgGetResult(
    CloudNativePgGetStatus Status,
    CloudNativePgClusterSnapshot? Snapshot = null,
    string? ErrorCode = null,
    string? ErrorSummary = null)
{
    public static CloudNativePgGetResult FoundCluster(CloudNativePgClusterSnapshot snapshot)
        => new(CloudNativePgGetStatus.Found, Snapshot: snapshot);

    public static CloudNativePgGetResult Missing()
        => new(CloudNativePgGetStatus.NotFound);

    public static CloudNativePgGetResult Timeout(string errorCode, string errorSummary)
        => new(CloudNativePgGetStatus.TimeoutUncertain, ErrorCode: errorCode, ErrorSummary: errorSummary);

    public static CloudNativePgGetResult Transient(string errorCode, string errorSummary)
        => new(CloudNativePgGetStatus.TransientError, ErrorCode: errorCode, ErrorSummary: errorSummary);

    public static CloudNativePgGetResult Terminal(string errorCode, string errorSummary)
        => new(CloudNativePgGetStatus.TerminalError, ErrorCode: errorCode, ErrorSummary: errorSummary);
}

internal enum CloudNativePgCreateStatus
{
    Created = 0,
    AlreadyExists = 1,
    TimeoutUncertain = 2,
    TransientError = 3,
    TerminalError = 4
}

internal sealed record CloudNativePgCreateResult(
    CloudNativePgCreateStatus Status,
    CloudNativePgClusterSnapshot? Snapshot = null,
    string? ErrorCode = null,
    string? ErrorSummary = null)
{
    public static CloudNativePgCreateResult CreatedCluster(CloudNativePgClusterSnapshot snapshot)
        => new(CloudNativePgCreateStatus.Created, Snapshot: snapshot);

    public static CloudNativePgCreateResult ConflictAlreadyExists()
        => new(CloudNativePgCreateStatus.AlreadyExists);

    public static CloudNativePgCreateResult Uncertain(string errorCode, string errorSummary)
        => new(CloudNativePgCreateStatus.TimeoutUncertain, ErrorCode: errorCode, ErrorSummary: errorSummary);

    public static CloudNativePgCreateResult Transient(string errorCode, string errorSummary)
        => new(CloudNativePgCreateStatus.TransientError, ErrorCode: errorCode, ErrorSummary: errorSummary);

    public static CloudNativePgCreateResult Terminal(string errorCode, string errorSummary)
        => new(CloudNativePgCreateStatus.TerminalError, ErrorCode: errorCode, ErrorSummary: errorSummary);
}

/// <summary>
/// Abstracción mínima del cliente Kubernetes para gestionar recursos <c>Cluster</c> de CloudNativePG en un namespace único.
/// </summary>
internal interface ICloudNativePgClusterClient
{
    void EnsureInitialized();

    Task<CloudNativePgGetResult> GetClusterAsync(
        string @namespace,
        string clusterName,
        CancellationToken cancellationToken);

    Task<CloudNativePgCreateResult> CreateClusterAsync(
        string @namespace,
        IReadOnlyDictionary<string, object?> manifest,
        CancellationToken cancellationToken);
}
