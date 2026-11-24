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
}
