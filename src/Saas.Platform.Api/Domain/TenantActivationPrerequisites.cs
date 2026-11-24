namespace Saas.Platform.Api.Domain;

/// <summary>
/// Prerrequisitos obligatorios exigidos por ADR-0002 y ADR-0003 antes de habilitar
/// operativamente una clínica en estado ACTIVA.
/// Identity READY es necesario pero no suficiente por sí solo.
/// </summary>
public sealed record TenantActivationPrerequisites(
    bool TenantDatabaseInstanceReady,
    bool ServiceDatabasesPrepared,
    bool IdentityInitialAdminReady,
    string? InitialAdminAccountId);
