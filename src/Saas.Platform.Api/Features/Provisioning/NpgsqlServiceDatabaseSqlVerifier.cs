using System.Net.Sockets;
using Npgsql;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Implementación Npgsql TLS para aplicar <c>REVOKE CONNECT ... FROM PUBLIC</c> como propietario
/// de cada base y comprobar en vivo el aislamiento de <c>pg_hba</c> y ACLs sin usar superusuario.
/// </summary>
internal sealed class NpgsqlServiceDatabaseSqlVerifier(
    CloudNativePgOptions options,
    ILogger<NpgsqlServiceDatabaseSqlVerifier> logger) : IServiceDatabaseSqlVerifier
{
    public async Task<ServiceDatabaseSqlVerificationResult> VerifyPreLoginPgHbaEnforcementAsync(
        string writeHost,
        int port,
        CancellationToken cancellationToken)
    {
        var probes = new (string Role, string Database, SslMode SslMode)[]
        {
            (ServiceDatabaseTarget.Identity.RoleName, ServiceDatabaseTarget.Clinical.DatabaseName, SslMode.Require),
            (ServiceDatabaseTarget.Identity.RoleName, ServiceDatabaseTarget.Identity.DatabaseName, SslMode.Disable),
            (ServiceDatabaseTarget.Identity.RoleName, "postgres", SslMode.Require),
            (ServiceDatabaseTarget.Clinical.RoleName, ServiceDatabaseTarget.Identity.DatabaseName, SslMode.Require),
            (ServiceDatabaseTarget.Clinical.RoleName, ServiceDatabaseTarget.Clinical.DatabaseName, SslMode.Disable),
            (ServiceDatabaseTarget.Clinical.RoleName, "postgres", SslMode.Require)
        };

        foreach (var probe in probes)
        {
            var probeResult = await ProbeDeniedConnectionAsync(
                writeHost,
                port,
                probe.Database,
                probe.Role,
                "__plat05b_nologin_probe__",
                probe.SslMode,
                requireExplicitPgHbaReject: true,
                cancellationToken);

            if (probeResult.Outcome != ServiceDatabaseSqlVerificationOutcome.Passed)
            {
                return probeResult;
            }
        }

        return ServiceDatabaseSqlVerificationResult.Passed();
    }

    public async Task<ServiceDatabaseSqlVerificationResult> RevokePublicConnectAndVerifyOwnerSessionAsync(
        string writeHost,
        int port,
        ServiceDatabaseTarget target,
        ServiceDatabaseTarget otherTarget,
        string password,
        CancellationToken cancellationToken)
    {
        var databaseIdentifier = ResolveAllowedDatabaseIdentifier(target);
        var connectionString = BuildConnectionString(writeHost, port, target.DatabaseName, target.RoleName, password, SslMode.Require);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using (var revokeCommand = new NpgsqlCommand($"REVOKE CONNECT ON DATABASE \"{databaseIdentifier}\" FROM PUBLIC;", connection))
            {
                revokeCommand.CommandTimeout = options.ServiceDatabases.SqlCommandTimeoutSeconds;
                await revokeCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string verifySql = """
                SELECT current_database()::text,
                       current_user::text,
                       pg_is_in_recovery(),
                       r.rolcanlogin,
                       r.rolsuper,
                       r.rolcreatedb,
                       r.rolcreaterole,
                       r.rolreplication,
                       r.rolbypassrls,
                       pg_get_userbyid(d.datdba)::text,
                       has_database_privilege('public', @dbName, 'CONNECT'),
                       has_database_privilege(@ownerRole, @dbName, 'CONNECT'),
                       has_database_privilege(@otherRole, @dbName, 'CONNECT')
                FROM pg_database AS d
                JOIN pg_roles AS r ON r.rolname = @ownerRole
                WHERE d.datname = @dbName;
                """;

            await using var verifyCommand = new NpgsqlCommand(verifySql, connection);
            verifyCommand.CommandTimeout = options.ServiceDatabases.SqlCommandTimeoutSeconds;
            verifyCommand.Parameters.AddWithValue("dbName", target.DatabaseName);
            verifyCommand.Parameters.AddWithValue("ownerRole", target.RoleName);
            verifyCommand.Parameters.AddWithValue("otherRole", otherTarget.RoleName);

            await using var reader = await verifyCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return ServiceDatabaseSqlVerificationResult.Terminal(
                    "PG_ACL_VERIFICATION_FAILED",
                    $"Could not verify PostgreSQL catalog metadata for service '{target.ServiceKey}'.");
            }

            var currentDb = reader.GetString(0);
            var currentUser = reader.GetString(1);
            var isReplica = reader.GetBoolean(2);
            var canLogin = reader.GetBoolean(3);
            var isSuper = reader.GetBoolean(4);
            var canCreateDb = reader.GetBoolean(5);
            var canCreateRole = reader.GetBoolean(6);
            var hasReplication = reader.GetBoolean(7);
            var hasBypassRls = reader.GetBoolean(8);
            var ownerName = reader.GetString(9);
            var publicCanConnect = reader.GetBoolean(10);
            var ownerCanConnect = reader.GetBoolean(11);
            var otherCanConnect = reader.GetBoolean(12);

            if (!string.Equals(currentDb, target.DatabaseName, StringComparison.Ordinal)
                || !string.Equals(currentUser, target.RoleName, StringComparison.Ordinal)
                || isReplica
                || !canLogin
                || isSuper
                || canCreateDb
                || canCreateRole
                || hasReplication
                || hasBypassRls
                || !string.Equals(ownerName, target.RoleName, StringComparison.Ordinal)
                || publicCanConnect
                || !ownerCanConnect
                || otherCanConnect)
            {
                logger.LogWarning(
                    "PostgreSQL ownership/ACL verification failed for service {ServiceKey} against {Host}:{Port}.",
                    target.ServiceKey,
                    writeHost,
                    port);

                return ServiceDatabaseSqlVerificationResult.Terminal(
                    "PG_ACL_VERIFICATION_FAILED",
                    $"PostgreSQL ownership, privileges, or CONNECT ACL invariants failed for service '{target.ServiceKey}'.");
            }

            return ServiceDatabaseSqlVerificationResult.Passed();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException ex)
        {
            logger.LogWarning(
                "PostgreSQL owner session failed for service {ServiceKey} with SQLSTATE {SqlState}.",
                target.ServiceKey,
                ex.SqlState);

            return ex.SqlState is "28000" or "28P01" or "3D000" or "57P03"
                ? ServiceDatabaseSqlVerificationResult.Transient(
                    "PG_INFRASTRUCTURE_UNREACHABLE",
                    $"PostgreSQL owner session for '{target.ServiceKey}' is not ready yet (SQLSTATE {ex.SqlState}).")
                : ServiceDatabaseSqlVerificationResult.Terminal(
                    "PG_ACL_VERIFICATION_FAILED",
                    $"PostgreSQL rejected owner ACL hardening for service '{target.ServiceKey}' (SQLSTATE {ex.SqlState}).");
        }
        catch (Exception ex) when (IsTransportOrTimeoutException(ex))
        {
            logger.LogWarning(
                "PostgreSQL owner connection for service {ServiceKey} encountered transport error ({ExceptionType}).",
                target.ServiceKey,
                ex.GetType().Name);

            return ServiceDatabaseSqlVerificationResult.Transient(
                "PG_INFRASTRUCTURE_UNREACHABLE",
                $"Tenant PostgreSQL instance was unreachable while hardening service '{target.ServiceKey}'.");
        }
    }

    public async Task<ServiceDatabaseSqlVerificationResult> VerifyPostLoginNegativeMatrixAsync(
        string writeHost,
        int port,
        IReadOnlyDictionary<string, string> servicePasswords,
        CancellationToken cancellationToken)
    {
        var identityPassword = servicePasswords[ServiceDatabaseTarget.Identity.ServiceKey];
        var clinicalPassword = servicePasswords[ServiceDatabaseTarget.Clinical.ServiceKey];

        var matrix = new (string Role, string Password, string Database, SslMode SslMode)[]
        {
            (ServiceDatabaseTarget.Identity.RoleName, identityPassword, ServiceDatabaseTarget.Identity.DatabaseName, SslMode.Disable),
            (ServiceDatabaseTarget.Clinical.RoleName, clinicalPassword, ServiceDatabaseTarget.Clinical.DatabaseName, SslMode.Disable),
            (ServiceDatabaseTarget.Identity.RoleName, identityPassword, ServiceDatabaseTarget.Clinical.DatabaseName, SslMode.Require),
            (ServiceDatabaseTarget.Clinical.RoleName, clinicalPassword, ServiceDatabaseTarget.Identity.DatabaseName, SslMode.Require),
            (ServiceDatabaseTarget.Identity.RoleName, identityPassword, "postgres", SslMode.Require),
            (ServiceDatabaseTarget.Clinical.RoleName, clinicalPassword, "postgres", SslMode.Require),
            (ServiceDatabaseTarget.Identity.RoleName, identityPassword, "template1", SslMode.Require),
            (ServiceDatabaseTarget.Clinical.RoleName, clinicalPassword, "template1", SslMode.Require)
        };

        foreach (var testCase in matrix)
        {
            var probeResult = await ProbeDeniedConnectionAsync(
                writeHost,
                port,
                testCase.Database,
                testCase.Role,
                testCase.Password,
                testCase.SslMode,
                requireExplicitPgHbaReject: testCase.SslMode == SslMode.Disable,
                cancellationToken);

            if (probeResult.Outcome != ServiceDatabaseSqlVerificationOutcome.Passed)
            {
                return probeResult;
            }
        }

        return ServiceDatabaseSqlVerificationResult.Passed();
    }

    private async Task<ServiceDatabaseSqlVerificationResult> ProbeDeniedConnectionAsync(
        string writeHost,
        int port,
        string database,
        string username,
        string password,
        SslMode sslMode,
        bool requireExplicitPgHbaReject,
        CancellationToken cancellationToken)
    {
        var connectionString = BuildConnectionString(writeHost, port, database, username, password, sslMode);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            logger.LogWarning(
                "Unauthorized PostgreSQL connection unexpectedly succeeded for role {Role} database {Database} SslMode {SslMode}.",
                username,
                database,
                sslMode);

            return ServiceDatabaseSqlVerificationResult.Terminal(
                "PG_ISOLATION_VERIFICATION_FAILED",
                $"Unauthorized PostgreSQL connection succeeded for '{username}' on database '{database}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException ex)
        {
            var isPgHbaReject = string.Equals(ex.SqlState, "28000", StringComparison.Ordinal)
                && ex.MessageText.Contains("pg_hba.conf rejects connection", StringComparison.OrdinalIgnoreCase);

            if (isPgHbaReject)
            {
                return ServiceDatabaseSqlVerificationResult.Passed();
            }

            if (!requireExplicitPgHbaReject && string.Equals(ex.SqlState, "42501", StringComparison.Ordinal))
            {
                return ServiceDatabaseSqlVerificationResult.Passed();
            }

            logger.LogWarning(
                "Negative PostgreSQL probe ({Role}->{Database}, SslMode={SslMode}) did not fail with expected policy enforcement (SQLSTATE={SqlState}).",
                username,
                database,
                sslMode,
                ex.SqlState);

            return string.Equals(ex.SqlState, "57P03", StringComparison.Ordinal)
                ? ServiceDatabaseSqlVerificationResult.Transient(
                    "PG_INFRASTRUCTURE_UNREACHABLE",
                    "Tenant PostgreSQL instance is still starting up during isolation verification.")
                : ServiceDatabaseSqlVerificationResult.Transient(
                    "CNPG_PGHBA_NOT_APPLIED",
                    $"Expected pg_hba TLS/database restriction was not yet active for '{username}' on '{database}' (SQLSTATE {ex.SqlState}).");
        }
        catch (Exception ex) when (IsTransportOrTimeoutException(ex))
        {
            logger.LogWarning(
                "Negative PostgreSQL probe ({Role}->{Database}, SslMode={SslMode}) encountered transport error ({ExceptionType}).",
                username,
                database,
                sslMode,
                ex.GetType().Name);

            return ServiceDatabaseSqlVerificationResult.Transient(
                "PG_INFRASTRUCTURE_UNREACHABLE",
                "Tenant PostgreSQL instance was unreachable during isolation verification.");
        }
    }

    private string BuildConnectionString(
        string writeHost,
        int port,
        string database,
        string username,
        string password,
        SslMode sslMode)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = writeHost,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            SslMode = sslMode,
            Pooling = false,
            Timeout = options.ServiceDatabases.SqlConnectTimeoutSeconds,
            CommandTimeout = options.ServiceDatabases.SqlCommandTimeoutSeconds
        };

        return builder.ConnectionString;
    }

    private static string ResolveAllowedDatabaseIdentifier(ServiceDatabaseTarget target)
        => target.DatabaseName switch
        {
            "identity" => "identity",
            "clinical" => "clinical",
            _ => throw new InvalidOperationException($"Unsupported target database identifier '{target.DatabaseName}'.")
        };

    private static bool IsTransportOrTimeoutException(Exception ex)
        => ex is TimeoutException or SocketException
            || (ex is NpgsqlException npgsql && npgsql is not PostgresException)
            || ex is OperationCanceledException;
}
