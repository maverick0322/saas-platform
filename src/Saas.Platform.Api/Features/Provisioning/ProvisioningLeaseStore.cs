using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Persistence;

namespace Saas.Platform.Api.Features.Provisioning;

public sealed record ProvisioningLeaseToken(
    Guid OperationId,
    string LeaseOwner,
    long LeaseVersion);

public sealed record AcquiredProvisioningOperation(
    ProvisioningLeaseToken Token,
    Guid TenantId,
    string TenantPublicId,
    string TenantName,
    string ProvisioningRequestId,
    string InitialAdminFullName,
    string InitialAdminEmail,
    int AttemptCount,
    DateTimeOffset LeaseUntilUtc,
    ProvisioningStageState InfrastructureStageState,
    ProvisioningStageState ServiceDatabasesStageState,
    ProvisioningStageState ClinicalStageState,
    ProvisioningStageState IdentityStageState,
    string? InfrastructureResourceRef,
    string? ClinicalMigrationVersion,
    string? InitialAdminAccountId)
{
    public ProvisioningStageState GetStageState(ProvisioningStage stage) => stage switch
    {
        ProvisioningStage.InfrastructureInstance => InfrastructureStageState,
        ProvisioningStage.ServiceDatabases => ServiceDatabasesStageState,
        ProvisioningStage.ClinicalPreparation => ClinicalStageState,
        ProvisioningStage.IdentityPreparation => IdentityStageState,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported provisioning stage.")
    };

    public AcquiredProvisioningOperation WithStageConfirmed(ProvisioningStage stage, string? technicalReference) =>
        stage switch
        {
            ProvisioningStage.InfrastructureInstance => this with
            {
                InfrastructureStageState = ProvisioningStageState.Confirmed,
                InfrastructureResourceRef = technicalReference ?? InfrastructureResourceRef
            },
            ProvisioningStage.ServiceDatabases => this with
            {
                ServiceDatabasesStageState = ProvisioningStageState.Confirmed
            },
            ProvisioningStage.ClinicalPreparation => this with
            {
                ClinicalStageState = ProvisioningStageState.Confirmed,
                ClinicalMigrationVersion = technicalReference ?? ClinicalMigrationVersion
            },
            ProvisioningStage.IdentityPreparation => this with
            {
                IdentityStageState = ProvisioningStageState.Confirmed,
                InitialAdminAccountId = technicalReference ?? InitialAdminAccountId
            },
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported provisioning stage.")
        };
}

public enum ResumeProvisioningOperationOutcome
{
    Resumed = 0,
    NotFound = 1,
    TenantNotPendingAdmin = 2,
    InvalidStatusOrReason = 3,
    AlreadyResumedOrNoPendingStage = 4,
    ActiveLease = 5,
    AttemptBudgetExhausted = 6,
    NextStageAdapterNotConfigured = 7
}

public sealed record ResumeProvisioningOperationResult(
    ResumeProvisioningOperationOutcome Outcome,
    Guid OperationId,
    Guid? TenantId = null,
    ProvisioningStage? NextStage = null,
    int? AttemptCount = null,
    string? Summary = null)
{
    public bool Resumed => Outcome == ResumeProvisioningOperationOutcome.Resumed;
}

public enum RecoverPlat05bSerializationOutcome
{
    Recovered = 0,
    UnauthorizedOperation = 1,
    NotFound = 2,
    TenantNotPendingAdmin = 3,
    ActiveLease = 4,
    InvalidStatusOrReason = 5,
    InvalidStageStateOrReference = 6,
    AttemptBudgetExhausted = 7,
    NextStageAdapterNotConfigured = 8
}

public sealed record RecoverPlat05bSerializationResult(
    RecoverPlat05bSerializationOutcome Outcome,
    Guid OperationId,
    Guid? TenantId = null,
    int? AttemptCount = null,
    string? Summary = null)
{
    public bool Recovered => Outcome == RecoverPlat05bSerializationOutcome.Recovered;
}

/// <summary>
/// Contrato acotado del único incidente histórico autorizado para recuperación excepcional de PLAT-05B.
/// TEMPORAL: retirar este modo antes de promocionar a <c>main</c>.
/// </summary>
public static class Plat05bSerializationIncidentContract
{
    public static readonly Guid AuthorizedTenantId = Guid.Parse("01a214af-fddc-713e-a908-c1f690486d90");
    public static readonly Guid AuthorizedOperationId = Guid.Parse("01a214af-fddc-75c8-9c5e-7f2317a4e6c2");
    public const string AuthorizedInfrastructureResourceRef =
        "saas-tenant-databases/pg-01a214affddc713ea908c1f690486d90@84fb9b6c-b40a-4c85-b822-810907498f73";
    public const string ExpectedErrorCode = "CNPG_ROLE_CONFLICT";
    public const string ExpectedErrorSummary =
        "DatabaseRole specification for service 'identity' is incompatible with isolated non-admin ownership.";
    public const int ExpectedAttemptCount = 2;
}

/// <summary>
/// Almacén atómico de arrendamientos (lease + fencing) y progreso durable sobre PostgreSQL.
/// Todas las transacciones y bloqueos de fila se liberan inmediatamente antes de invocar adaptadores externos.
/// </summary>
public sealed class ProvisioningLeaseStore(PlatformDbContext dbContext)
{
    private readonly string _connectionString = dbContext.Database.GetConnectionString()
        ?? throw new InvalidOperationException("PlatformDatabase connection string is not configured.");

    public async Task<int> MarkExpiredExhaustedLeasesAsync(
        DateTimeOffset utcNow,
        int maxTotalAttempts,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE platform.tenant_provisioning_operations
            SET status = 'FAILED_RETRYABLE',
                lease_owner = NULL,
                lease_until_utc = NULL,
                next_attempt_at_utc = NULL,
                manual_intervention_required = TRUE,
                infrastructure_stage_state = CASE
                    WHEN infrastructure_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE infrastructure_stage_state
                END,
                service_databases_stage_state = CASE
                    WHEN infrastructure_stage_state = 'CONFIRMED' AND service_databases_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE service_databases_stage_state
                END,
                clinical_stage_state = CASE
                    WHEN infrastructure_stage_state = 'CONFIRMED' AND service_databases_stage_state = 'CONFIRMED' AND clinical_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE clinical_stage_state
                END,
                identity_stage_state = CASE
                    WHEN infrastructure_stage_state = 'CONFIRMED' AND service_databases_stage_state = 'CONFIRMED' AND clinical_stage_state = 'CONFIRMED' AND identity_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE identity_stage_state
                END,
                last_error_code = 'RETRIES_EXHAUSTED_LEASE_EXPIRED',
                last_error_summary = 'Provisioning lease expired after reaching the maximum configured attempts; manual intervention is required.',
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            WHERE status = 'IN_PROGRESS'
              AND lease_until_utc <= @utcNow
              AND attempt_count >= @maxTotalAttempts;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("utcNow", utcNow.ToUniversalTime());
        command.Parameters.AddWithValue("maxTotalAttempts", maxTotalAttempts);
        command.Parameters.AddWithValue("concurrencyStamp", Guid.NewGuid());

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AcquiredProvisioningOperation?> TryAcquireNextAsync(
        string workerId,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        int maxTotalAttempts,
        CancellationToken cancellationToken)
    {
        var normalizedWorkerId = ValidateWorkerId(workerId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        var nowUtc = utcNow.ToUniversalTime();
        var leaseUntilUtc = nowUtc.Add(leaseDuration);

        const string acquireSql = """
            WITH candidate AS (
                SELECT op.id
                FROM platform.tenant_provisioning_operations AS op
                INNER JOIN platform.tenants AS t ON t.id = op.tenant_id
                WHERE t.operational_state = 'PENDIENTE_ADMIN'
                  AND op.attempt_count < @maxTotalAttempts
                  AND (
                      op.status = 'PENDING'
                      OR (op.status = 'FAILED_RETRYABLE'
                          AND op.manual_intervention_required = FALSE
                          AND (op.next_attempt_at_utc IS NULL OR op.next_attempt_at_utc <= @utcNow))
                      OR (op.status = 'IN_PROGRESS'
                          AND op.lease_until_utc <= @utcNow)
                  )
                ORDER BY op.created_at_utc ASC, op.id ASC
                FOR UPDATE OF op SKIP LOCKED
                LIMIT 1
            )
            UPDATE platform.tenant_provisioning_operations AS target
            SET status = 'IN_PROGRESS',
                lease_owner = @workerId,
                lease_until_utc = @leaseUntilUtc,
                lease_version = target.lease_version + 1,
                attempt_count = target.attempt_count + 1,
                last_attempt_at_utc = @utcNow,
                next_attempt_at_utc = NULL,
                infrastructure_stage_state = CASE
                    WHEN target.status = 'IN_PROGRESS' AND target.infrastructure_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE target.infrastructure_stage_state
                END,
                service_databases_stage_state = CASE
                    WHEN target.status = 'IN_PROGRESS' AND target.infrastructure_stage_state = 'CONFIRMED' AND target.service_databases_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE target.service_databases_stage_state
                END,
                clinical_stage_state = CASE
                    WHEN target.status = 'IN_PROGRESS' AND target.infrastructure_stage_state = 'CONFIRMED' AND target.service_databases_stage_state = 'CONFIRMED' AND target.clinical_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE target.clinical_stage_state
                END,
                identity_stage_state = CASE
                    WHEN target.status = 'IN_PROGRESS' AND target.infrastructure_stage_state = 'CONFIRMED' AND target.service_databases_stage_state = 'CONFIRMED' AND target.clinical_stage_state = 'CONFIRMED' AND target.identity_stage_state = 'PENDING' THEN 'VERIFICATION_REQUIRED'
                    ELSE target.identity_stage_state
                END,
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            FROM candidate
            WHERE target.id = candidate.id
            RETURNING target.id;
            """;

        const string loadSql = """
            SELECT
                op.id,
                op.lease_owner,
                op.lease_version,
                op.tenant_id,
                t.public_id,
                t.name,
                op.provisioning_request_id,
                op.initial_admin_full_name,
                op.initial_admin_email,
                op.attempt_count,
                op.lease_until_utc,
                op.infrastructure_stage_state,
                op.service_databases_stage_state,
                op.clinical_stage_state,
                op.identity_stage_state,
                op.infrastructure_resource_ref,
                op.clinical_migration_version,
                op.initial_admin_account_id
            FROM platform.tenant_provisioning_operations AS op
            INNER JOIN platform.tenants AS t ON t.id = op.tenant_id
            WHERE op.id = @operationId;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        Guid? acquiredId;
        await using (var acquireCommand = new NpgsqlCommand(acquireSql, connection, transaction))
        {
            acquireCommand.Parameters.AddWithValue("workerId", normalizedWorkerId);
            acquireCommand.Parameters.AddWithValue("utcNow", nowUtc);
            acquireCommand.Parameters.AddWithValue("leaseUntilUtc", leaseUntilUtc);
            acquireCommand.Parameters.AddWithValue("maxTotalAttempts", maxTotalAttempts);
            acquireCommand.Parameters.AddWithValue("concurrencyStamp", Guid.NewGuid());

            var scalar = await acquireCommand.ExecuteScalarAsync(cancellationToken);
            acquiredId = scalar is Guid id ? id : null;
        }

        if (acquiredId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        AcquiredProvisioningOperation? acquired = null;
        await using (var loadCommand = new NpgsqlCommand(loadSql, connection, transaction))
        {
            loadCommand.Parameters.AddWithValue("operationId", acquiredId.Value);

            await using var reader = await loadCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var token = new ProvisioningLeaseToken(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetInt64(2));

                acquired = new AcquiredProvisioningOperation(
                    Token: token,
                    TenantId: reader.GetGuid(3),
                    TenantPublicId: reader.GetString(4),
                    TenantName: reader.GetString(5),
                    ProvisioningRequestId: reader.GetString(6),
                    InitialAdminFullName: reader.GetString(7),
                    InitialAdminEmail: reader.GetString(8),
                    AttemptCount: reader.GetInt32(9),
                    LeaseUntilUtc: reader.GetFieldValue<DateTimeOffset>(10),
                    InfrastructureStageState: ProvisioningStageStateExtensions.ParseCode(reader.GetString(11)),
                    ServiceDatabasesStageState: ProvisioningStageStateExtensions.ParseCode(reader.GetString(12)),
                    ClinicalStageState: ProvisioningStageStateExtensions.ParseCode(reader.GetString(13)),
                    IdentityStageState: ProvisioningStageStateExtensions.ParseCode(reader.GetString(14)),
                    InfrastructureResourceRef: reader.IsDBNull(15) ? null : reader.GetString(15),
                    ClinicalMigrationVersion: reader.IsDBNull(16) ? null : reader.GetString(16),
                    InitialAdminAccountId: reader.IsDBNull(17) ? null : reader.GetString(17));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return acquired;
    }

    public async Task<bool> TryRenewLeaseAsync(
        ProvisioningLeaseToken token,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        var nowUtc = utcNow.ToUniversalTime();
        var newLeaseUntilUtc = nowUtc.Add(leaseDuration);

        const string sql = """
            UPDATE platform.tenant_provisioning_operations
            SET lease_until_utc = @newLeaseUntilUtc,
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            WHERE id = @id
              AND status = 'IN_PROGRESS'
              AND lease_owner = @leaseOwner
              AND lease_version = @leaseVersion
              AND lease_until_utc > @utcNow;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        AddFenceParameters(command, token, nowUtc);
        command.Parameters.AddWithValue("newLeaseUntilUtc", newLeaseUntilUtc);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryRecordStageConfirmedAsync(
        ProvisioningLeaseToken token,
        ProvisioningStage stage,
        string? technicalReference,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var sanitizedRef = TenantProvisioningOperation.SanitizeTechnicalReference(technicalReference);
        var nowUtc = utcNow.ToUniversalTime();

        var stageColumn = GetStageColumnName(stage);
        var refColumn = GetTechnicalReferenceColumnName(stage);

        var refAssignment = refColumn is not null && sanitizedRef is not null
            ? $", {refColumn} = @technicalReference"
            : string.Empty;

        var sql = $"""
            UPDATE platform.tenant_provisioning_operations
            SET {stageColumn} = 'CONFIRMED'{refAssignment},
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            WHERE id = @id
              AND status = 'IN_PROGRESS'
              AND lease_owner = @leaseOwner
              AND lease_version = @leaseVersion
              AND lease_until_utc > @utcNow;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        AddFenceParameters(command, token, nowUtc);
        if (refColumn is not null && sanitizedRef is not null)
        {
            command.Parameters.AddWithValue("technicalReference", sanitizedRef);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryReleaseWithRecoverableFailureAsync(
        ProvisioningLeaseToken token,
        ProvisioningStage? markStageVerificationRequired,
        string errorCode,
        string errorSummary,
        DateTimeOffset utcNow,
        DateTimeOffset? nextAttemptAtUtc,
        bool manualInterventionRequired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var normalizedCode = TenantProvisioningOperation.NormalizeErrorCode(errorCode);
        var sanitizedSummary = TenantProvisioningOperation.SanitizeDiagnosticSummary(errorSummary);
        var nowUtc = utcNow.ToUniversalTime();

        var stageAssignment = markStageVerificationRequired is { } stage
            ? $", {GetStageColumnName(stage)} = 'VERIFICATION_REQUIRED'"
            : string.Empty;

        var sql = $"""
            UPDATE platform.tenant_provisioning_operations
            SET status = 'FAILED_RETRYABLE',
                lease_owner = NULL,
                lease_until_utc = NULL,
                next_attempt_at_utc = @nextAttemptAtUtc,
                manual_intervention_required = @manualInterventionRequired,
                last_error_code = @errorCode,
                last_error_summary = @errorSummary{stageAssignment},
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            WHERE id = @id
              AND status = 'IN_PROGRESS'
              AND lease_owner = @leaseOwner
              AND lease_version = @leaseVersion
              AND lease_until_utc > @utcNow;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        AddFenceParameters(command, token, nowUtc);
        command.Parameters.AddWithValue(
            "nextAttemptAtUtc",
            nextAttemptAtUtc.HasValue ? nextAttemptAtUtc.Value.ToUniversalTime() : DBNull.Value);
        command.Parameters.AddWithValue("manualInterventionRequired", manualInterventionRequired);
        command.Parameters.AddWithValue("errorCode", normalizedCode);
        command.Parameters.AddWithValue("errorSummary", sanitizedSummary);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryReleaseWithTerminalFailureAsync(
        ProvisioningLeaseToken token,
        string errorCode,
        string errorSummary,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var normalizedCode = TenantProvisioningOperation.NormalizeErrorCode(errorCode);
        var sanitizedSummary = TenantProvisioningOperation.SanitizeDiagnosticSummary(errorSummary);
        var nowUtc = utcNow.ToUniversalTime();

        const string sql = """
            UPDATE platform.tenant_provisioning_operations
            SET status = 'FAILED_TERMINAL',
                lease_owner = NULL,
                lease_until_utc = NULL,
                next_attempt_at_utc = NULL,
                manual_intervention_required = TRUE,
                last_error_code = @errorCode,
                last_error_summary = @errorSummary,
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            WHERE id = @id
              AND status = 'IN_PROGRESS'
              AND lease_owner = @leaseOwner
              AND lease_version = @leaseVersion
              AND lease_until_utc > @utcNow;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        AddFenceParameters(command, token, nowUtc);
        command.Parameters.AddWithValue("errorCode", normalizedCode);
        command.Parameters.AddWithValue("errorSummary", sanitizedSummary);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddFenceParameters(NpgsqlCommand command, ProvisioningLeaseToken token, DateTimeOffset utcNow)
    {
        command.Parameters.AddWithValue("id", token.OperationId);
        command.Parameters.AddWithValue("leaseOwner", token.LeaseOwner);
        command.Parameters.AddWithValue("leaseVersion", token.LeaseVersion);
        command.Parameters.AddWithValue("utcNow", utcNow);
        command.Parameters.AddWithValue("concurrencyStamp", Guid.NewGuid());
    }

    private static string ValidateWorkerId(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("WorkerId is required.", nameof(workerId));
        }

        var trimmed = workerId.Trim();
        if (trimmed.Length > TenantProvisioningOperation.LeaseOwnerMaxLength)
        {
            throw new ArgumentException(
                $"WorkerId must not exceed {TenantProvisioningOperation.LeaseOwnerMaxLength} characters.",
                nameof(workerId));
        }

        return trimmed;
    }

    private static string GetStageColumnName(ProvisioningStage stage) => stage switch
    {
        ProvisioningStage.InfrastructureInstance => "infrastructure_stage_state",
        ProvisioningStage.ServiceDatabases => "service_databases_stage_state",
        ProvisioningStage.ClinicalPreparation => "clinical_stage_state",
        ProvisioningStage.IdentityPreparation => "identity_stage_state",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported provisioning stage.")
    };

    private static string? GetTechnicalReferenceColumnName(ProvisioningStage stage) => stage switch
    {
        ProvisioningStage.InfrastructureInstance => "infrastructure_resource_ref",
        ProvisioningStage.ServiceDatabases => null,
        ProvisioningStage.ClinicalPreparation => "clinical_migration_version",
        ProvisioningStage.IdentityPreparation => "initial_admin_account_id",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported provisioning stage.")
    };

    public async Task<ResumeProvisioningOperationResult> TryResumeAdapterStoppedOperationAsync(
        Guid operationId,
        DateTimeOffset utcNow,
        int maxTotalAttempts,
        Func<ProvisioningStage, bool> isStageSupported,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.NotFound,
                operationId,
                Summary: "OperationId must be a non-empty UUID.");
        }

        ArgumentNullException.ThrowIfNull(isStageSupported);

        var nowUtc = utcNow.ToUniversalTime();
        var supportsInfrastructure = isStageSupported(ProvisioningStage.InfrastructureInstance);
        var supportsServiceDatabases = isStageSupported(ProvisioningStage.ServiceDatabases);
        var supportsClinical = isStageSupported(ProvisioningStage.ClinicalPreparation);
        var supportsIdentity = isStageSupported(ProvisioningStage.IdentityPreparation);

        const string resumeSql = """
            UPDATE platform.tenant_provisioning_operations AS op
            SET manual_intervention_required = FALSE,
                next_attempt_at_utc = @utcNow,
                last_error_code = NULL,
                last_error_summary = NULL,
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            FROM platform.tenants AS t
            WHERE op.id = @operationId
              AND t.id = op.tenant_id
              AND t.operational_state = 'PENDIENTE_ADMIN'
              AND op.status = 'FAILED_RETRYABLE'
              AND op.manual_intervention_required = TRUE
              AND op.last_error_code = 'ADAPTER_NOT_CONFIGURED'
              AND op.lease_owner IS NULL
              AND (op.lease_until_utc IS NULL OR op.lease_until_utc <= @utcNow)
              AND op.attempt_count < @maxTotalAttempts
              AND CASE
                    WHEN op.infrastructure_stage_state <> 'CONFIRMED' THEN @supportsInfrastructure
                    WHEN op.service_databases_stage_state <> 'CONFIRMED' THEN @supportsServiceDatabases
                    WHEN op.clinical_stage_state <> 'CONFIRMED' THEN @supportsClinical
                    WHEN op.identity_stage_state <> 'CONFIRMED' THEN @supportsIdentity
                    ELSE FALSE
                  END
            RETURNING op.tenant_id,
                      op.attempt_count,
                      op.infrastructure_stage_state,
                      op.service_databases_stage_state,
                      op.clinical_stage_state,
                      op.identity_stage_state;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(resumeSql, connection))
        {
            command.Parameters.AddWithValue("operationId", operationId);
            command.Parameters.AddWithValue("utcNow", nowUtc);
            command.Parameters.AddWithValue("concurrencyStamp", Guid.NewGuid());
            command.Parameters.AddWithValue("maxTotalAttempts", maxTotalAttempts);
            command.Parameters.AddWithValue("supportsInfrastructure", supportsInfrastructure);
            command.Parameters.AddWithValue("supportsServiceDatabases", supportsServiceDatabases);
            command.Parameters.AddWithValue("supportsClinical", supportsClinical);
            command.Parameters.AddWithValue("supportsIdentity", supportsIdentity);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var tenantId = reader.GetGuid(0);
                var attemptCount = reader.GetInt32(1);
                var nextStage = ResolveNextStageFromCodes(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5));

                return new ResumeProvisioningOperationResult(
                    ResumeProvisioningOperationOutcome.Resumed,
                    operationId,
                    tenantId,
                    nextStage,
                    attemptCount,
                    $"Operation re-queued for ordinary worker acquisition at stage '{nextStage?.ToCode()}'.");
            }
        }

        const string inspectSql = """
            SELECT op.tenant_id,
                   t.operational_state,
                   op.status,
                   op.manual_intervention_required,
                   op.last_error_code,
                   op.lease_owner,
                   op.lease_until_utc,
                   op.attempt_count,
                   op.infrastructure_stage_state,
                   op.service_databases_stage_state,
                   op.clinical_stage_state,
                   op.identity_stage_state,
                   op.next_attempt_at_utc
            FROM platform.tenant_provisioning_operations AS op
            INNER JOIN platform.tenants AS t ON t.id = op.tenant_id
            WHERE op.id = @operationId;
            """;

        await using var inspectCommand = new NpgsqlCommand(inspectSql, connection);
        inspectCommand.Parameters.AddWithValue("operationId", operationId);

        await using var inspectReader = await inspectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await inspectReader.ReadAsync(cancellationToken))
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.NotFound,
                operationId,
                Summary: "Provisioning operation was not found.");
        }

        var existingTenantId = inspectReader.GetGuid(0);
        var operationalState = inspectReader.GetString(1);
        var status = inspectReader.GetString(2);
        var manualIntervention = inspectReader.GetBoolean(3);
        var lastErrorCode = inspectReader.IsDBNull(4) ? null : inspectReader.GetString(4);
        var leaseOwner = inspectReader.IsDBNull(5) ? null : inspectReader.GetString(5);
        var leaseUntil = inspectReader.IsDBNull(6) ? (DateTimeOffset?)null : inspectReader.GetFieldValue<DateTimeOffset>(6);
        var attempts = inspectReader.GetInt32(7);
        var existingNextStage = ResolveNextStageFromCodes(
            inspectReader.GetString(8),
            inspectReader.GetString(9),
            inspectReader.GetString(10),
            inspectReader.GetString(11));
        var nextAttemptAt = inspectReader.IsDBNull(12) ? (DateTimeOffset?)null : inspectReader.GetFieldValue<DateTimeOffset>(12);

        if (!string.Equals(operationalState, TenantOperationalStateExtensions.PendienteAdminCode, StringComparison.Ordinal))
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.TenantNotPendingAdmin,
                operationId,
                existingTenantId,
                existingNextStage,
                attempts,
                $"Tenant operational state '{operationalState}' is not eligible for resumption.");
        }

        if (leaseOwner is not null || (leaseUntil.HasValue && leaseUntil.Value > nowUtc)
            || string.Equals(status, ProvisioningOperationStatusExtensions.InProgressCode, StringComparison.Ordinal))
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.ActiveLease,
                operationId,
                existingTenantId,
                existingNextStage,
                attempts,
                "Operation currently has an active lease or is in progress.");
        }

        if (existingNextStage is null)
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.AlreadyResumedOrNoPendingStage,
                operationId,
                existingTenantId,
                null,
                attempts,
                "All provisioning stages are already confirmed.");
        }

        if (string.Equals(status, ProvisioningOperationStatusExtensions.FailedRetryableCode, StringComparison.Ordinal)
            && !manualIntervention
            && nextAttemptAt.HasValue)
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.AlreadyResumedOrNoPendingStage,
                operationId,
                existingTenantId,
                existingNextStage,
                attempts,
                "Operation is already scheduled for worker acquisition.");
        }

        if (!string.Equals(status, ProvisioningOperationStatusExtensions.FailedRetryableCode, StringComparison.Ordinal)
            || !string.Equals(lastErrorCode, "ADAPTER_NOT_CONFIGURED", StringComparison.Ordinal))
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.InvalidStatusOrReason,
                operationId,
                existingTenantId,
                existingNextStage,
                attempts,
                $"Operation status '{status}' and reason '{lastErrorCode ?? "NONE"}' are not eligible for controlled adapter resumption.");
        }

        if (!manualIntervention)
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.AlreadyResumedOrNoPendingStage,
                operationId,
                existingTenantId,
                existingNextStage,
                attempts,
                "Operation is already scheduled for worker acquisition.");
        }

        if (attempts >= maxTotalAttempts)
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.AttemptBudgetExhausted,
                operationId,
                existingTenantId,
                existingNextStage,
                attempts,
                $"Attempt count ({attempts}) reached configured budget ({maxTotalAttempts}).");
        }

        if (!isStageSupported(existingNextStage.Value))
        {
            return new ResumeProvisioningOperationResult(
                ResumeProvisioningOperationOutcome.NextStageAdapterNotConfigured,
                operationId,
                existingTenantId,
                existingNextStage,
                attempts,
                $"Next pending stage '{existingNextStage.Value.ToCode()}' is not enabled in current worker configuration.");
        }

        return new ResumeProvisioningOperationResult(
            ResumeProvisioningOperationOutcome.InvalidStatusOrReason,
            operationId,
            existingTenantId,
            existingNextStage,
            attempts,
            "Operation state changed concurrently during inspection.");
    }

    public async Task<RecoverPlat05bSerializationResult> TryRecoverPlat05bSerializationIncidentAsync(
        Guid operationId,
        DateTimeOffset utcNow,
        int maxTotalAttempts,
        bool supportsServiceDatabases,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || operationId != Plat05bSerializationIncidentContract.AuthorizedOperationId)
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.UnauthorizedOperation,
                operationId,
                Summary: "Temporary PLAT-05B serialization recovery is restricted exclusively to the authorized lab operation.");
        }

        if (!supportsServiceDatabases)
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.NextStageAdapterNotConfigured,
                operationId,
                Summary: "CloudNativePG ServiceDatabases adapter is not enabled in current configuration.");
        }

        var nowUtc = utcNow.ToUniversalTime();

        const string recoverSql = """
            UPDATE platform.tenant_provisioning_operations AS op
            SET status = 'FAILED_RETRYABLE',
                manual_intervention_required = FALSE,
                next_attempt_at_utc = @utcNow,
                last_error_code = NULL,
                last_error_summary = NULL,
                updated_at_utc = @utcNow,
                concurrency_stamp = @concurrencyStamp
            FROM platform.tenants AS t
            WHERE op.id = @authorizedOperationId
              AND op.id = @operationId
              AND t.id = op.tenant_id
              AND op.tenant_id = @authorizedTenantId
              AND t.operational_state = 'PENDIENTE_ADMIN'
              AND op.status = 'FAILED_TERMINAL'
              AND op.manual_intervention_required = TRUE
              AND op.last_error_code = @expectedErrorCode
              AND op.last_error_summary = @expectedErrorSummary
              AND op.attempt_count = @expectedAttemptCount
              AND op.attempt_count < @maxTotalAttempts
              AND op.infrastructure_stage_state = 'CONFIRMED'
              AND op.infrastructure_resource_ref = @authorizedInfrastructureResourceRef
              AND op.service_databases_stage_state = 'PENDING'
              AND op.clinical_stage_state = 'PENDING'
              AND op.identity_stage_state = 'PENDING'
              AND op.lease_owner IS NULL
              AND op.lease_until_utc IS NULL
              AND op.next_attempt_at_utc IS NULL
              AND @supportsServiceDatabases = TRUE
            RETURNING op.tenant_id, op.attempt_count;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(recoverSql, connection))
        {
            command.Parameters.AddWithValue("authorizedOperationId", Plat05bSerializationIncidentContract.AuthorizedOperationId);
            command.Parameters.AddWithValue("operationId", operationId);
            command.Parameters.AddWithValue("authorizedTenantId", Plat05bSerializationIncidentContract.AuthorizedTenantId);
            command.Parameters.AddWithValue("expectedErrorCode", Plat05bSerializationIncidentContract.ExpectedErrorCode);
            command.Parameters.AddWithValue("expectedErrorSummary", Plat05bSerializationIncidentContract.ExpectedErrorSummary);
            command.Parameters.AddWithValue("expectedAttemptCount", Plat05bSerializationIncidentContract.ExpectedAttemptCount);
            command.Parameters.AddWithValue("authorizedInfrastructureResourceRef", Plat05bSerializationIncidentContract.AuthorizedInfrastructureResourceRef);
            command.Parameters.AddWithValue("maxTotalAttempts", maxTotalAttempts);
            command.Parameters.AddWithValue("supportsServiceDatabases", supportsServiceDatabases);
            command.Parameters.AddWithValue("utcNow", nowUtc);
            command.Parameters.AddWithValue("concurrencyStamp", Guid.NewGuid());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var tenantId = reader.GetGuid(0);
                var attemptCount = reader.GetInt32(1);

                return new RecoverPlat05bSerializationResult(
                    RecoverPlat05bSerializationOutcome.Recovered,
                    operationId,
                    tenantId,
                    attemptCount,
                    "Authorized PLAT-05B serialization incident re-queued for ordinary worker lease acquisition without advancing attempt_count.");
            }
        }

        const string inspectSql = """
            SELECT op.tenant_id,
                   t.operational_state,
                   op.status,
                   op.manual_intervention_required,
                   op.last_error_code,
                   op.last_error_summary,
                   op.lease_owner,
                   op.lease_until_utc,
                   op.attempt_count,
                   op.infrastructure_stage_state,
                   op.infrastructure_resource_ref,
                   op.service_databases_stage_state,
                   op.clinical_stage_state,
                   op.identity_stage_state,
                   op.next_attempt_at_utc
            FROM platform.tenant_provisioning_operations AS op
            INNER JOIN platform.tenants AS t ON t.id = op.tenant_id
            WHERE op.id = @operationId;
            """;

        await using var inspectCommand = new NpgsqlCommand(inspectSql, connection);
        inspectCommand.Parameters.AddWithValue("operationId", operationId);

        await using var inspectReader = await inspectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await inspectReader.ReadAsync(cancellationToken))
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.NotFound,
                operationId,
                Summary: "Provisioning operation was not found.");
        }

        var existingTenantId = inspectReader.GetGuid(0);
        var operationalState = inspectReader.GetString(1);
        var status = inspectReader.GetString(2);
        var manualIntervention = inspectReader.GetBoolean(3);
        var lastErrorCode = inspectReader.IsDBNull(4) ? null : inspectReader.GetString(4);
        var lastErrorSummary = inspectReader.IsDBNull(5) ? null : inspectReader.GetString(5);
        var leaseOwner = inspectReader.IsDBNull(6) ? null : inspectReader.GetString(6);
        var leaseUntil = inspectReader.IsDBNull(7) ? (DateTimeOffset?)null : inspectReader.GetFieldValue<DateTimeOffset>(7);
        var attempts = inspectReader.GetInt32(8);
        var infrastructureState = inspectReader.GetString(9);
        var infrastructureResourceRef = inspectReader.IsDBNull(10) ? null : inspectReader.GetString(10);
        var serviceDatabasesState = inspectReader.GetString(11);
        var clinicalState = inspectReader.GetString(12);
        var identityState = inspectReader.GetString(13);
        var nextAttemptAt = inspectReader.IsDBNull(14) ? (DateTimeOffset?)null : inspectReader.GetFieldValue<DateTimeOffset>(14);

        if (existingTenantId != Plat05bSerializationIncidentContract.AuthorizedTenantId)
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.UnauthorizedOperation,
                operationId,
                existingTenantId,
                attempts,
                "Operation tenant identity does not match the authorized PLAT-05B incident target.");
        }

        if (!string.Equals(operationalState, TenantOperationalStateExtensions.PendienteAdminCode, StringComparison.Ordinal))
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.TenantNotPendingAdmin,
                operationId,
                existingTenantId,
                attempts,
                $"Tenant operational state '{operationalState}' is not eligible for exceptional recovery.");
        }

        if (leaseOwner is not null
            || leaseUntil.HasValue
            || string.Equals(status, ProvisioningOperationStatusExtensions.InProgressCode, StringComparison.Ordinal))
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.ActiveLease,
                operationId,
                existingTenantId,
                attempts,
                "Operation currently holds a lease or is in progress.");
        }

        if (!string.Equals(infrastructureState, "CONFIRMED", StringComparison.Ordinal)
            || !string.Equals(infrastructureResourceRef, Plat05bSerializationIncidentContract.AuthorizedInfrastructureResourceRef, StringComparison.Ordinal)
            || !string.Equals(serviceDatabasesState, "PENDING", StringComparison.Ordinal)
            || !string.Equals(clinicalState, "PENDING", StringComparison.Ordinal)
            || !string.Equals(identityState, "PENDING", StringComparison.Ordinal))
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.InvalidStageStateOrReference,
                operationId,
                existingTenantId,
                attempts,
                "Operation stage states or infrastructure reference do not match the authorized PLAT-05B incident snapshot.");
        }

        if (attempts >= maxTotalAttempts)
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.AttemptBudgetExhausted,
                operationId,
                existingTenantId,
                attempts,
                $"Attempt count ({attempts}) reached configured budget ({maxTotalAttempts}).");
        }

        if (!string.Equals(status, ProvisioningOperationStatusExtensions.FailedTerminalCode, StringComparison.Ordinal)
            || !manualIntervention
            || nextAttemptAt.HasValue
            || !string.Equals(lastErrorCode, Plat05bSerializationIncidentContract.ExpectedErrorCode, StringComparison.Ordinal)
            || !string.Equals(lastErrorSummary, Plat05bSerializationIncidentContract.ExpectedErrorSummary, StringComparison.Ordinal)
            || attempts != Plat05bSerializationIncidentContract.ExpectedAttemptCount)
        {
            return new RecoverPlat05bSerializationResult(
                RecoverPlat05bSerializationOutcome.InvalidStatusOrReason,
                operationId,
                existingTenantId,
                attempts,
                $"Operation status '{status}', error '{lastErrorCode ?? "NONE"}', or attempt_count ({attempts}) does not match the authorized PLAT-05B incident.");
        }

        return new RecoverPlat05bSerializationResult(
            RecoverPlat05bSerializationOutcome.InvalidStatusOrReason,
            operationId,
            existingTenantId,
            attempts,
            "Operation state changed concurrently during inspection.");
    }

    private static ProvisioningStage? ResolveNextStageFromCodes(
        string infrastructureState,
        string serviceDatabasesState,
        string clinicalState,
        string identityState)
    {
        if (!string.Equals(infrastructureState, "CONFIRMED", StringComparison.Ordinal))
        {
            return ProvisioningStage.InfrastructureInstance;
        }

        if (!string.Equals(serviceDatabasesState, "CONFIRMED", StringComparison.Ordinal))
        {
            return ProvisioningStage.ServiceDatabases;
        }

        if (!string.Equals(clinicalState, "CONFIRMED", StringComparison.Ordinal))
        {
            return ProvisioningStage.ClinicalPreparation;
        }

        if (!string.Equals(identityState, "CONFIRMED", StringComparison.Ordinal))
        {
            return ProvisioningStage.IdentityPreparation;
        }

        return null;
    }
}
