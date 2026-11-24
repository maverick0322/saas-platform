namespace Saas.Platform.Api.Domain;

/// <summary>
/// Estado durable individual de cada etapa del aprovisionamiento de una clínica.
/// </summary>
public enum ProvisioningStageState
{
    Pending = 1,
    VerificationRequired = 2,
    Confirmed = 3
}

public static class ProvisioningStageStateExtensions
{
    public const string PendingCode = "PENDING";
    public const string VerificationRequiredCode = "VERIFICATION_REQUIRED";
    public const string ConfirmedCode = "CONFIRMED";

    public static string ToCode(this ProvisioningStageState state) => state switch
    {
        ProvisioningStageState.Pending => PendingCode,
        ProvisioningStageState.VerificationRequired => VerificationRequiredCode,
        ProvisioningStageState.Confirmed => ConfirmedCode,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported provisioning stage state.")
    };

    public static ProvisioningStageState ParseCode(string code) => code switch
    {
        PendingCode => ProvisioningStageState.Pending,
        VerificationRequiredCode => ProvisioningStageState.VerificationRequired,
        ConfirmedCode => ProvisioningStageState.Confirmed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown provisioning stage state code.")
    };
}
