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
            "INFRASTRUCTURE_UNAVAILABLE" or "CNPG_WAIT" or "CNPG_API_UNAVAILABLE" =>
                "Dedicated tenant database infrastructure is not ready yet.",
            "CNPG_API_TIMEOUT_UNCERTAIN" or "CNPG_CREATE_TIMEOUT_UNCERTAIN" or "CNPG_CLUSTER_DISAPPEARED" =>
                "Dedicated database provisioning timed out and requires state verification before retrying.",
            "CNPG_PGHBA_NOT_APPLIED" or "CNPG_ROLE_WAIT" or "CNPG_DATABASE_WAIT"
                or "CNPG_DISTRIBUTION_CONCURRENCY_EXHAUSTED" or "PG_INFRASTRUCTURE_UNREACHABLE" =>
                "Service database preparation is waiting for PostgreSQL isolation or reconciliation rules to take effect.",
            "CNPG_RBAC_FORBIDDEN" or "CNPG_INVALID_MANIFEST" or "CNPG_CLUSTER_MISSING"
                or "CNPG_CLUSTER_OWNERSHIP_CONFLICT" or "CNPG_CLUSTER_IDENTITY_MISMATCH"
                or "CNPG_CLUSTER_DELETING" or "CNPG_CLUSTER_SPEC_MISMATCH"
                or "CNPG_CLUSTER_UNRECOVERABLE" or "CNPG_INVALID_CLUSTER_PAYLOAD" or "CNPG_API_REJECTED"
                or "CNPG_PGHBA_NOT_CONFIGURED" or "CNPG_ROLE_CONFLICT" or "CNPG_DATABASE_CONFLICT"
                or "CNPG_AUTH_SECRET_CONFLICT" or "CNPG_DISTRIBUTION_SECRET_MISSING"
                or "CNPG_DISTRIBUTION_SECRET_CONFLICT"
                or "PG_ACL_VERIFICATION_FAILED" or "PG_ISOLATION_VERIFICATION_FAILED" =>
                "Dedicated database provisioning stopped due to a Kubernetes or PostgreSQL validation error and requires administrative review.",
            "INVALID_TECHNICAL_REFERENCE" =>
                "Infrastructure provisioning returned an invalid cluster reference and requires administrative verification.",
            "ADAPTER_NOT_CONFIGURED" =>
                "Initial provisioning progressed as far as currently configured adapters and is waiting for subsequent provisioning stages.",
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
