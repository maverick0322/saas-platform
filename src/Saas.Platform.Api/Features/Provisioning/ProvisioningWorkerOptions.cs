using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Provisioning;

public enum ProvisioningExecutionMode
{
    Unconfigured = 0,
    CloudNativePg = 1
}

/// <summary>
/// Configuración explícita del núcleo trabajador de aprovisionamiento (PLAT-02C / PLAT-05A).
/// Permanece deshabilitado y sin adaptador externo activo por defecto hasta habilitar el modo correspondiente.
/// </summary>
public sealed class ProvisioningWorkerOptions
{
    public const string SectionName = "ProvisioningWorker";

    private readonly string _fallbackWorkerId =
        $"platform-worker-{Environment.MachineName}-{Guid.NewGuid():N}"[..Math.Min(TenantProvisioningOperation.LeaseOwnerMaxLength, $"platform-worker-{Environment.MachineName}-".Length + 8)];

    public bool Enabled { get; set; }

    public ProvisioningExecutionMode Mode { get; set; } = ProvisioningExecutionMode.Unconfigured;

    public int MaxAutomaticRetries { get; set; } = 3;

    public int LeaseDurationSeconds { get; set; } = 60;

    public int InitialRetryDelaySeconds { get; set; } = 15;

    public int MaxRetryDelaySeconds { get; set; } = 300;

    public int PollIntervalSeconds { get; set; } = 5;

    public string? WorkerId { get; set; }

    public int MaxTotalAttempts => 1 + MaxAutomaticRetries;

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public TimeSpan LeaseRenewalInterval =>
        TimeSpan.FromMilliseconds(Math.Max(100, LeaseDuration.TotalMilliseconds / 3.0));

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public string ResolveWorkerId() =>
        string.IsNullOrWhiteSpace(WorkerId) ? _fallbackWorkerId : WorkerId.Trim();

    public TimeSpan CalculateRetryBackoff(int completedAttemptCount)
    {
        if (completedAttemptCount <= 0)
        {
            return TimeSpan.FromSeconds(InitialRetryDelaySeconds);
        }

        var exponent = Math.Min(completedAttemptCount - 1, 10);
        var delaySeconds = (double)InitialRetryDelaySeconds * (1 << exponent);
        var boundedSeconds = Math.Min(delaySeconds, MaxRetryDelaySeconds);
        return TimeSpan.FromSeconds(boundedSeconds);
    }

    public void Validate()
    {
        if (MaxAutomaticRetries < 0)
        {
            throw new InvalidOperationException("ProvisioningWorker:MaxAutomaticRetries must be >= 0.");
        }

        if (LeaseDurationSeconds < 1)
        {
            throw new InvalidOperationException("ProvisioningWorker:LeaseDurationSeconds must be >= 1.");
        }

        if (InitialRetryDelaySeconds < 1)
        {
            throw new InvalidOperationException("ProvisioningWorker:InitialRetryDelaySeconds must be >= 1.");
        }

        if (MaxRetryDelaySeconds < InitialRetryDelaySeconds)
        {
            throw new InvalidOperationException(
                "ProvisioningWorker:MaxRetryDelaySeconds must be >= InitialRetryDelaySeconds.");
        }

        if (PollIntervalSeconds < 1)
        {
            throw new InvalidOperationException("ProvisioningWorker:PollIntervalSeconds must be >= 1.");
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new InvalidOperationException("ProvisioningWorker:Mode must be a supported execution mode.");
        }

        var resolvedWorkerId = ResolveWorkerId();
        if (resolvedWorkerId.Length > TenantProvisioningOperation.LeaseOwnerMaxLength)
        {
            throw new InvalidOperationException(
                $"ProvisioningWorker:WorkerId must not exceed {TenantProvisioningOperation.LeaseOwnerMaxLength} characters.");
        }
    }
}
