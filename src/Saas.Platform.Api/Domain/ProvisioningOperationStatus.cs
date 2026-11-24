namespace Saas.Platform.Api.Domain;

/// <summary>
/// Estado técnico durable de la operación inicial de aprovisionamiento de una clínica.
/// </summary>
public enum ProvisioningOperationStatus
{
    Pending = 1,
    InProgress = 2,
    FailedRetryable = 3,
    FailedTerminal = 4,
    Completed = 5
}

public static class ProvisioningOperationStatusExtensions
{
    public const string PendingCode = "PENDING";
    public const string InProgressCode = "IN_PROGRESS";
    public const string FailedRetryableCode = "FAILED_RETRYABLE";
    public const string FailedTerminalCode = "FAILED_TERMINAL";
    public const string CompletedCode = "COMPLETED";

    public static string ToCode(this ProvisioningOperationStatus status) => status switch
    {
        ProvisioningOperationStatus.Pending => PendingCode,
        ProvisioningOperationStatus.InProgress => InProgressCode,
        ProvisioningOperationStatus.FailedRetryable => FailedRetryableCode,
        ProvisioningOperationStatus.FailedTerminal => FailedTerminalCode,
        ProvisioningOperationStatus.Completed => CompletedCode,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported provisioning operation status.")
    };

    public static ProvisioningOperationStatus ParseCode(string code) => code switch
    {
        PendingCode => ProvisioningOperationStatus.Pending,
        InProgressCode => ProvisioningOperationStatus.InProgress,
        FailedRetryableCode => ProvisioningOperationStatus.FailedRetryable,
        FailedTerminalCode => ProvisioningOperationStatus.FailedTerminal,
        CompletedCode => ProvisioningOperationStatus.Completed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown provisioning operation status code.")
    };
}
