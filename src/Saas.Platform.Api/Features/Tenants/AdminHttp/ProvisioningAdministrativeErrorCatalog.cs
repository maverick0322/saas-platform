using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

/// <summary>
/// Mensajes administrativos controlados para exposición HTTP en <c>lastErrorSummary</c>.
/// Nunca copia diagnósticos internos crudos ni mensajes arbitrarios persistidos por el trabajador.
/// </summary>
internal static class ProvisioningAdministrativeErrorCatalog
{
    public static string? ResolveControlledSummary(
        string? lastErrorCode,
        ProvisioningOperationStatus status,
        bool manualInterventionRequired)
    {
        if (string.IsNullOrWhiteSpace(lastErrorCode))
        {
            return null;
        }

        return lastErrorCode switch
        {
            "IDENTITY_PROVISIONING_CONFLICT" =>
                "Identity reported a payload conflict for the provisioning request and requires administrative review.",
            "INITIAL_ADMIN_ALREADY_EXISTS" or "DUPLICATE_STAFF_EMAIL" =>
                "Identity reported an existing clinic administrator conflict and requires administrative review.",
            "EMAIL_DELIVERY_FAILED" =>
                "Identity could not deliver the initial administrator credentials and will require retry or verification.",
            "IDENTITY_TIMEOUT" or "IDENTITY_UNAVAILABLE" =>
                "Identity is temporarily unavailable to complete initial administrator provisioning.",
            "INFRASTRUCTURE_UNAVAILABLE" or "CNPG_WAIT" =>
                "Dedicated tenant database infrastructure is not ready yet.",
            "SERVICE_DATABASES_ERROR" =>
                "Required per-service database preparation failed and needs controlled recovery.",
            "CLINICAL_PREPARATION_ERROR" =>
                "Clinical schema preparation could not be confirmed for the tenant.",
            "LEASE_EXPIRED_VERIFICATION_REQUIRED" =>
                "A previous provisioning attempt timed out; stage state must be verified before continuing.",
            _ when manualInterventionRequired || status == ProvisioningOperationStatus.FailedTerminal =>
                "Initial provisioning stopped with a terminal error and requires administrative review.",
            _ =>
                "Initial provisioning encountered a recoverable error and is pending controlled retry."
        };
    }
}
