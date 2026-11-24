namespace Saas.Platform.Api.Domain;

/// <summary>
/// Etapas ordenadas del flujo durable de aprovisionamiento inicial de una clínica (PLAT-02B/02C).
/// </summary>
public enum ProvisioningStage
{
    InfrastructureInstance = 1,
    ServiceDatabases = 2,
    ClinicalPreparation = 3,
    IdentityPreparation = 4
}

public static class ProvisioningStageExtensions
{
    public const string InfrastructureInstanceCode = "INFRASTRUCTURE_INSTANCE";
    public const string ServiceDatabasesCode = "SERVICE_DATABASES";
    public const string ClinicalPreparationCode = "CLINICAL_PREPARATION";
    public const string IdentityPreparationCode = "IDENTITY_PREPARATION";

    public static IReadOnlyList<ProvisioningStage> OrderedStages { get; } =
    [
        ProvisioningStage.InfrastructureInstance,
        ProvisioningStage.ServiceDatabases,
        ProvisioningStage.ClinicalPreparation,
        ProvisioningStage.IdentityPreparation
    ];

    public static string ToCode(this ProvisioningStage stage) => stage switch
    {
        ProvisioningStage.InfrastructureInstance => InfrastructureInstanceCode,
        ProvisioningStage.ServiceDatabases => ServiceDatabasesCode,
        ProvisioningStage.ClinicalPreparation => ClinicalPreparationCode,
        ProvisioningStage.IdentityPreparation => IdentityPreparationCode,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported provisioning stage.")
    };

    public static ProvisioningStage ParseCode(string code) => code switch
    {
        InfrastructureInstanceCode => ProvisioningStage.InfrastructureInstance,
        ServiceDatabasesCode => ProvisioningStage.ServiceDatabases,
        ClinicalPreparationCode => ProvisioningStage.ClinicalPreparation,
        IdentityPreparationCode => ProvisioningStage.IdentityPreparation,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown provisioning stage code.")
    };
}
