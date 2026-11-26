namespace Saas.Platform.Api.Features.Provisioning;

internal enum ServiceDatabaseSqlVerificationOutcome
{
    Passed = 0,
    TransientFailure = 1,
    TerminalFailure = 2
}

internal sealed record ServiceDatabaseSqlVerificationResult(
    ServiceDatabaseSqlVerificationOutcome Outcome,
    string? ErrorCode = null,
    string? ErrorSummary = null)
{
    public static ServiceDatabaseSqlVerificationResult Passed()
        => new(ServiceDatabaseSqlVerificationOutcome.Passed);

    public static ServiceDatabaseSqlVerificationResult Transient(string errorCode, string errorSummary)
        => new(ServiceDatabaseSqlVerificationOutcome.TransientFailure, errorCode, errorSummary);

    public static ServiceDatabaseSqlVerificationResult Terminal(string errorCode, string errorSummary)
        => new(ServiceDatabaseSqlVerificationOutcome.TerminalFailure, errorCode, errorSummary);
}

/// <summary>
/// Verifica en vivo contra el servidor PostgreSQL del tenant (sin superusuario):
/// 1) reglas <c>pg_hba</c> antes de habilitar <c>LOGIN</c>,
/// 2) <c>REVOKE CONNECT ... FROM PUBLIC</c> y privilegios como propietario sobre TLS, y
/// 3) rechazo efectivo de conexiones cruzadas y sin TLS tras habilitar <c>LOGIN</c>.
/// </summary>
internal interface IServiceDatabaseSqlVerifier
{
    Task<ServiceDatabaseSqlVerificationResult> VerifyPreLoginPgHbaEnforcementAsync(
        string writeHost,
        int port,
        CancellationToken cancellationToken);

    Task<ServiceDatabaseSqlVerificationResult> RevokePublicConnectAndVerifyOwnerSessionAsync(
        string writeHost,
        int port,
        ServiceDatabaseTarget target,
        ServiceDatabaseTarget otherTarget,
        string password,
        CancellationToken cancellationToken);

    Task<ServiceDatabaseSqlVerificationResult> VerifyPostLoginNegativeMatrixAsync(
        string writeHost,
        int port,
        IReadOnlyDictionary<string, string> servicePasswords,
        CancellationToken cancellationToken);
}
