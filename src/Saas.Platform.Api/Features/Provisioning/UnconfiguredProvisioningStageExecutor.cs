namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Implementación por defecto en producción mientras CloudNativePG, Identity y Clinical
/// no dispongan de adaptadores reales configurados y ratificados (PLAT-02C).
/// </summary>
public sealed class UnconfiguredProvisioningStageExecutor : IProvisioningStageExecutor
{
    private static readonly ProvisioningStageResult NotConfiguredResult = new(
        ProvisioningStageOutcomeKind.AdapterNotConfigured,
        ErrorCode: "ADAPTER_NOT_CONFIGURED",
        ErrorSummary: "External provisioning adapters (CloudNativePG, Identity, Clinical) are not configured in PLAT-02C.");

    public bool SupportsRealProvisioning => false;

    public Task<ProvisioningStageResult> ExecuteStageAsync(
        ProvisioningStageContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(NotConfiguredResult);
}
