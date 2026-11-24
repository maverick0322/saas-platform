namespace Saas.Platform.Api.Domain;

/// <summary>
/// Estado operativo de una clínica en el catálogo administrativo global de Platform.
/// Valores alineados 1:1 con el contrato Protobuf Platform–Identity v1.
/// </summary>
public enum TenantOperationalState
{
    PendienteAdmin = 1,
    Activa = 2,
    Suspendida = 3
}

public static class TenantOperationalStateExtensions
{
    public const string PendienteAdminCode = "PENDIENTE_ADMIN";
    public const string ActivaCode = "ACTIVA";
    public const string SuspendidaCode = "SUSPENDIDA";

    public static string ToCode(this TenantOperationalState state) => state switch
    {
        TenantOperationalState.PendienteAdmin => PendienteAdminCode,
        TenantOperationalState.Activa => ActivaCode,
        TenantOperationalState.Suspendida => SuspendidaCode,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported tenant operational state.")
    };

    public static TenantOperationalState ParseCode(string code) => code switch
    {
        PendienteAdminCode => TenantOperationalState.PendienteAdmin,
        ActivaCode => TenantOperationalState.Activa,
        SuspendidaCode => TenantOperationalState.Suspendida,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown tenant operational state code.")
    };
}
