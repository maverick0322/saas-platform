using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class TenantProvisioningIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task RegisterTenant_ValidInput_PersistsAtomicallyTenantAndPendingOperationWithDistinctEmails()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-prov-{Guid.NewGuid():N}"[..30];

        RegisterTenantResult result;
        await using (var writeContext = fixture.CreateDbContext())
        {
            var handler = new RegisterTenantHandler(
                writeContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            result = await handler.HandleAsync(
                new RegisterTenantCommand(
                    PublicId: publicId,
                    Name: "Clínica Sintética Aprovisionamiento",
                    ContactEmail: "facturacion.contacto@clinica-prov.test",
                    InitialAdminFullName: "Dr. Carlos Navarro",
                    InitialAdminEmail: "admin.navarro@clinica-prov.test",
                    MaxActiveAccounts: 25),
                ct);
        }

        Assert.Equal(RegisterTenantStatus.Created, result.Status);
        Assert.NotNull(result.Tenant);
        Assert.Equal(TenantOperationalState.PendienteAdmin, result.Tenant.OperationalState);
        Assert.Equal("PENDIENTE_ADMIN", result.Tenant.OperationalStateCode);
        Assert.False(result.Tenant.MayAuthenticate);

        var summary = result.Tenant.InitialProvisioning;
        Assert.NotNull(summary);
        Assert.NotEqual(Guid.Empty, summary.OperationId);
        Assert.True(Guid.TryParseExact(summary.ProvisioningRequestId, "D", out _));
        Assert.Equal(ProvisioningOperationStatus.Pending, summary.Status);
        Assert.Equal("PENDING", summary.StatusCode);
        Assert.Equal("Dr. Carlos Navarro", summary.InitialAdminFullName);
        Assert.Equal("admin.navarro@clinica-prov.test", summary.InitialAdminEmail);
        Assert.NotEqual(result.Tenant.ContactEmail, summary.InitialAdminEmail);
        Assert.Equal(0, summary.AttemptCount);
        Assert.Null(summary.LastErrorCode);
        Assert.Null(summary.LastErrorSummary);

        await using var verifyContext = fixture.CreateDbContext();
        var tenantCount = await verifyContext.Tenants.CountAsync(t => t.Id == result.Tenant.Id, ct);
        var opCount = await verifyContext.TenantProvisioningOperations.CountAsync(o => o.TenantId == result.Tenant.Id, ct);
        Assert.Equal(1, tenantCount);
        Assert.Equal(1, opCount);
    }

    [Fact]
    public async Task RegisterTenant_WithValidTenantButInvalidAdmin_PersistsNeitherEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-atomic-{Guid.NewGuid():N}"[..32];

        await using var context = fixture.CreateDbContext();
        var tenantsBefore = await context.Tenants.CountAsync(ct);
        var operationsBefore = await context.TenantProvisioningOperations.CountAsync(ct);

        var handler = new RegisterTenantHandler(
            context,
            TimeProvider.System,
            NullLogger<RegisterTenantHandler>.Instance);

        var result = await handler.HandleAsync(
            new RegisterTenantCommand(
                PublicId: publicId,
                Name: "Clínica Sintética Rollback",
                ContactEmail: "contacto@clinica-atomic.test",
                InitialAdminFullName: "A",
                InitialAdminEmail: "correo-no-valido",
                MaxActiveAccounts: 25),
            ct);

        Assert.Equal(RegisterTenantStatus.ValidationFailed, result.Status);
        Assert.Null(result.Tenant);
        Assert.Contains(result.ValidationErrors, e => e.Field == nameof(TenantProvisioningOperation.InitialAdminFullName));
        Assert.Contains(result.ValidationErrors, e => e.Field == nameof(TenantProvisioningOperation.InitialAdminEmail));

        Assert.Equal(tenantsBefore, await context.Tenants.CountAsync(ct));
        Assert.Equal(operationsBefore, await context.TenantProvisioningOperations.CountAsync(ct));
        Assert.False(await context.Tenants.AnyAsync(t => t.PublicId == publicId, ct));
    }

    [Fact]
    public async Task RegisterTenant_ConcurrentDuplicates_NeverCreateOrphanOrDuplicateOperations()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-oprace-{Guid.NewGuid():N}"[..32];

        using var barrier = new SemaphoreSlim(0, 2);

        async Task<RegisterTenantResult> AttemptAsync(string suffix)
        {
            await using var context = fixture.CreateDbContext();
            var handler = new RegisterTenantHandler(
                context,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            await barrier.WaitAsync(ct);
            return await handler.HandleAsync(
                new RegisterTenantCommand(
                    PublicId: publicId,
                    Name: $"Clínica Paralela {suffix}",
                    ContactEmail: $"contacto-{suffix}@clinica-oprace.test",
                    InitialAdminFullName: $"Admin Paralelo {suffix}",
                    InitialAdminEmail: $"admin-{suffix}@clinica-oprace.test",
                    MaxActiveAccounts: 25),
                ct);
        }

        var t1 = AttemptAsync("a");
        var t2 = AttemptAsync("b");
        barrier.Release(2);
        var results = await Task.WhenAll(t1, t2);

        var created = Assert.Single(results, r => r.Status == RegisterTenantStatus.Created);
        Assert.Single(results, r => r.Status == RegisterTenantStatus.Conflict);

        await using var verifyContext = fixture.CreateDbContext();
        var matchingTenants = await verifyContext.Tenants
            .Where(t => t.PublicId == publicId)
            .ToListAsync(ct);
        var tenant = Assert.Single(matchingTenants);
        Assert.Equal(created.Tenant!.Id, tenant.Id);

        var matchingOps = await verifyContext.TenantProvisioningOperations
            .Where(o => o.TenantId == tenant.Id)
            .ToListAsync(ct);
        var op = Assert.Single(matchingOps);
        Assert.Equal(created.Tenant.InitialProvisioning!.OperationId, op.Id);
        Assert.Equal(created.Tenant.InitialProvisioning.ProvisioningRequestId, op.ProvisioningRequestId);
    }

    [Fact]
    public async Task RestartWithNewDbContext_RecoversSameOperationAndProvisioningRequestId()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-reopen-{Guid.NewGuid():N}"[..32];

        RegisterTenantResult created;
        await using (var firstContext = fixture.CreateDbContext())
        {
            var registerHandler = new RegisterTenantHandler(
                firstContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            created = await registerHandler.HandleAsync(
                new RegisterTenantCommand(
                    PublicId: publicId,
                    Name: "Clínica Reinicio Contexto",
                    ContactEmail: "contacto@clinica-reopen.test",
                    InitialAdminFullName: "Dra. Lucía Torres",
                    InitialAdminEmail: "lucia.torres@clinica-reopen.test",
                    MaxActiveAccounts: 25),
                ct);
        }

        Assert.Equal(RegisterTenantStatus.Created, created.Status);
        var expected = created.Tenant!;

        await using var restartedContext = fixture.CreateDbContext();
        var queryHandler = new GetTenantByIdHandler(restartedContext);
        var reloaded = await queryHandler.HandleAsync(new GetTenantByIdQuery(expected.Id), ct);

        Assert.NotNull(reloaded);
        Assert.Equal(TenantOperationalState.PendienteAdmin, reloaded.OperationalState);
        Assert.False(reloaded.MayAuthenticate);
        Assert.NotNull(reloaded.InitialProvisioning);
        Assert.Equal(expected.InitialProvisioning!.OperationId, reloaded.InitialProvisioning.OperationId);
        Assert.Equal(expected.InitialProvisioning.ProvisioningRequestId, reloaded.InitialProvisioning.ProvisioningRequestId);
        Assert.Equal("Dra. Lucía Torres", reloaded.InitialProvisioning.InitialAdminFullName);
        Assert.Equal("lucia.torres@clinica-reopen.test", reloaded.InitialProvisioning.InitialAdminEmail);
        Assert.Equal(ProvisioningOperationStatus.Pending, reloaded.InitialProvisioning.Status);
    }

    [Fact]
    public async Task SimulatedRecoverableFailure_KeepsPendienteAdminPreservesRequestIdAndRedactsSecretsInPostgres()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-retry-{Guid.NewGuid():N}"[..31];

        RegisterTenantResult created;
        await using (var setupContext = fixture.CreateDbContext())
        {
            var handler = new RegisterTenantHandler(
                setupContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            created = await handler.HandleAsync(
                new RegisterTenantCommand(
                    PublicId: publicId,
                    Name: "Clínica Reintento Durable",
                    ContactEmail: "contacto@clinica-retry.test",
                    InitialAdminFullName: "Dr. Javier Soto",
                    InitialAdminEmail: "javier.soto@clinica-retry.test",
                    MaxActiveAccounts: 25),
                ct);
        }

        var tenantId = created.Tenant!.Id;
        var originalOperationId = created.Tenant.InitialProvisioning!.OperationId;
        var originalRequestId = created.Tenant.InitialProvisioning.ProvisioningRequestId;

        await using (var failureContext = fixture.CreateDbContext())
        {
            var op = await failureContext.TenantProvisioningOperations
                .SingleAsync(o => o.TenantId == tenantId, ct);

            op.RecordRecoverableFailure(
                "identity_timeout",
                "Timeout calling Identity endpoint; x-internal-service-key: FakeKeyShouldNotPersist password=SyntheticPass99!",
                DateTimeOffset.UtcNow);

            await failureContext.SaveChangesAsync(ct);
        }

        await using var verifyContext = fixture.CreateDbContext();
        var details = await new GetTenantByIdHandler(verifyContext).HandleAsync(new GetTenantByIdQuery(tenantId), ct);

        Assert.NotNull(details);
        Assert.Equal(TenantOperationalState.PendienteAdmin, details.OperationalState);
        Assert.False(details.MayAuthenticate);
        Assert.NotNull(details.InitialProvisioning);
        Assert.Equal(originalOperationId, details.InitialProvisioning.OperationId);
        Assert.Equal(originalRequestId, details.InitialProvisioning.ProvisioningRequestId);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, details.InitialProvisioning.Status);
        Assert.Equal("FAILED_RETRYABLE", details.InitialProvisioning.StatusCode);
        Assert.Equal(1, details.InitialProvisioning.AttemptCount);
        Assert.Equal("IDENTITY_TIMEOUT", details.InitialProvisioning.LastErrorCode);
        Assert.Equal("Dr. Javier Soto", details.InitialProvisioning.InitialAdminFullName);
        Assert.Equal("javier.soto@clinica-retry.test", details.InitialProvisioning.InitialAdminEmail);
        Assert.NotNull(details.InitialProvisioning.LastAttemptAtUtc);
        Assert.Null(details.InitialProvisioning.CompletedAtUtc);
        Assert.NotNull(details.InitialProvisioning.LastErrorSummary);
        Assert.DoesNotContain("FakeKeyShouldNotPersist", details.InitialProvisioning.LastErrorSummary);
        Assert.DoesNotContain("SyntheticPass99!", details.InitialProvisioning.LastErrorSummary);
        Assert.Contains("[REDACTED]", details.InitialProvisioning.LastErrorSummary);
    }

    [Fact]
    public async Task TenantProvisioningOperation_ConcurrentUpdate_ThrowsDbUpdateConcurrencyException()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-opconc-{Guid.NewGuid():N}"[..32];

        Guid tenantId;
        await using (var setupContext = fixture.CreateDbContext())
        {
            var handler = new RegisterTenantHandler(
                setupContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            var created = await handler.HandleAsync(
                new RegisterTenantCommand(
                    publicId,
                    "Clínica Concurrencia Operación",
                    "contacto@clinica-opconc.test",
                    "Admin Concurrencia",
                    "admin@clinica-opconc.test",
                    MaxActiveAccounts: 25),
                ct);

            tenantId = created.Tenant!.Id;
        }

        await using var contextA = fixture.CreateDbContext();
        await using var contextB = fixture.CreateDbContext();

        var opA = await contextA.TenantProvisioningOperations.SingleAsync(o => o.TenantId == tenantId, ct);
        var opB = await contextB.TenantProvisioningOperations.SingleAsync(o => o.TenantId == tenantId, ct);

        opA.RecordRecoverableFailure("CNPG_WAIT", "Cluster still reconciling.", DateTimeOffset.UtcNow);
        await contextA.SaveChangesAsync(ct);

        opB.RecordRecoverableFailure("IDENTITY_WAIT", "Identity unavailable.", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task PostgreSqlConstraints_EnforceUniqueTenantOperationForeignKeyAndChecks()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-pgchk-{Guid.NewGuid():N}"[..31];

        RegisterTenantResult created;
        await using (var setupContext = fixture.CreateDbContext())
        {
            var handler = new RegisterTenantHandler(
                setupContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            created = await handler.HandleAsync(
                new RegisterTenantCommand(
                    publicId,
                    "Clínica Restricciones PG",
                    "contacto@clinica-pgchk.test",
                    "Admin PG",
                    "admin@clinica-pgchk.test",
                    MaxActiveAccounts: 25),
                ct);
        }

        var existingTenantId = created.Tenant!.Id;
        var existingRequestId = created.Tenant.InitialProvisioning!.ProvisioningRequestId;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        // 1. Duplicate tenant_id in tenant_provisioning_operations -> 23505
        await using (var dupTenantOp = new NpgsqlCommand(
            """
            INSERT INTO platform.tenant_provisioning_operations
                (id, tenant_id, provisioning_request_id, initial_admin_full_name, initial_admin_email, status, attempt_count, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES
                (@id, @tenantId, @reqId, 'Admin Dup', 'dup@clinica.test', 'PENDING', 0, NOW(), NOW(), @stamp);
            """,
            connection))
        {
            dupTenantOp.Parameters.AddWithValue("id", Guid.NewGuid());
            dupTenantOp.Parameters.AddWithValue("tenantId", existingTenantId);
            dupTenantOp.Parameters.AddWithValue("reqId", Guid.NewGuid().ToString("D"));
            dupTenantOp.Parameters.AddWithValue("stamp", Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<PostgresException>(() => dupTenantOp.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
            Assert.Equal("ux_tenant_provisioning_operations_tenant_id", ex.ConstraintName);
        }

        // 2. Duplicate provisioning_request_id -> 23505
        var secondTenantId = Guid.NewGuid();
        await using (var insertSecondTenant = new NpgsqlCommand(
            """
            INSERT INTO platform.tenants
                (id, public_id, name, contact_email, operational_state, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES
                (@id, @pub, 'Clínica Auxiliar SQL', 'aux@clinica.test', 'PENDIENTE_ADMIN', NOW(), NOW(), @stamp);
            """,
            connection))
        {
            insertSecondTenant.Parameters.AddWithValue("id", secondTenantId);
            insertSecondTenant.Parameters.AddWithValue("pub", $"clinica-aux-{Guid.NewGuid():N}"[..28]);
            insertSecondTenant.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await insertSecondTenant.ExecuteNonQueryAsync(ct);
        }

        await using (var dupRequestIdCmd = new NpgsqlCommand(
            """
            INSERT INTO platform.tenant_provisioning_operations
                (id, tenant_id, provisioning_request_id, initial_admin_full_name, initial_admin_email, status, attempt_count, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES
                (@id, @tenantId, @reqId, 'Admin Aux', 'admin-aux@clinica.test', 'PENDING', 0, NOW(), NOW(), @stamp);
            """,
            connection))
        {
            dupRequestIdCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            dupRequestIdCmd.Parameters.AddWithValue("tenantId", secondTenantId);
            dupRequestIdCmd.Parameters.AddWithValue("reqId", existingRequestId);
            dupRequestIdCmd.Parameters.AddWithValue("stamp", Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<PostgresException>(() => dupRequestIdCmd.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
            Assert.Equal("ux_tenant_provisioning_operations_provisioning_request_id", ex.ConstraintName);
        }

        // 3. Non-existent tenant_id -> 23503 ForeignKeyViolation
        await using (var missingFkCmd = new NpgsqlCommand(
            """
            INSERT INTO platform.tenant_provisioning_operations
                (id, tenant_id, provisioning_request_id, initial_admin_full_name, initial_admin_email, status, attempt_count, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES
                (@id, @missingTenantId, @reqId, 'Admin Fantasma', 'ghost@clinica.test', 'PENDING', 0, NOW(), NOW(), @stamp);
            """,
            connection))
        {
            missingFkCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            missingFkCmd.Parameters.AddWithValue("missingTenantId", Guid.NewGuid());
            missingFkCmd.Parameters.AddWithValue("reqId", Guid.NewGuid().ToString("D"));
            missingFkCmd.Parameters.AddWithValue("stamp", Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<PostgresException>(() => missingFkCmd.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }

        // 4. Invalid operational_state on tenants -> 23514 CheckViolation
        await using (var badStateCmd = new NpgsqlCommand(
            """
            INSERT INTO platform.tenants
                (id, public_id, name, contact_email, operational_state, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES
                (@id, @pub, 'Clínica Estado Inválido', 'bad@clinica.test', 'LISTA', NOW(), NOW(), @stamp);
            """,
            connection))
        {
            badStateCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            badStateCmd.Parameters.AddWithValue("pub", $"clinica-badst-{Guid.NewGuid():N}"[..28]);
            badStateCmd.Parameters.AddWithValue("stamp", Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<PostgresException>(() => badStateCmd.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }

        // 5. Invalid status or negative attempt_count on tenant_provisioning_operations -> 23514 CheckViolation
        await using (var badOpCmd = new NpgsqlCommand(
            """
            INSERT INTO platform.tenant_provisioning_operations
                (id, tenant_id, provisioning_request_id, initial_admin_full_name, initial_admin_email, status, attempt_count, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES
                (@id, @tenantId, @reqId, 'Admin Bad', 'bad@clinica.test', 'UNKNOWN', -1, NOW(), NOW(), @stamp);
            """,
            connection))
        {
            badOpCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            badOpCmd.Parameters.AddWithValue("tenantId", secondTenantId);
            badOpCmd.Parameters.AddWithValue("reqId", Guid.NewGuid().ToString("D"));
            badOpCmd.Parameters.AddWithValue("stamp", Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<PostgresException>(() => badOpCmd.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task UpgradeMigration_FromPlat01SchemaWithExistingTenant_PreservesRowAndBackfillsPendienteAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var upgradeDbConnectionString = await fixture.CreateEmptyDatabaseAsync(
            $"plat02a_upgrade_{Guid.NewGuid():N}",
            ct);

        var legacyTenantId = Guid.NewGuid();
        const string legacyPublicId = "clinica-legado-01";

        await using (var plat01Context = fixture.CreateDbContext(upgradeDbConnectionString))
        {
            var migrator = plat01Context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261122180000_InitialTenantCatalog", ct);

            await using var conn = new NpgsqlConnection(upgradeDbConnectionString);
            await conn.OpenAsync(ct);

            await using var insertLegacy = new NpgsqlCommand(
                """
                INSERT INTO platform.tenants
                    (id, public_id, name, contact_email, created_at_utc, updated_at_utc, concurrency_stamp)
                VALUES
                    (@id, @publicId, 'Clínica Legado PLAT-01', 'legado@clinica.test', NOW(), NOW(), @stamp);
                """,
                conn);
            insertLegacy.Parameters.AddWithValue("id", legacyTenantId);
            insertLegacy.Parameters.AddWithValue("publicId", legacyPublicId);
            insertLegacy.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await insertLegacy.ExecuteNonQueryAsync(ct);
        }

        await using var upgradedContext = fixture.CreateDbContext(upgradeDbConnectionString);
        await upgradedContext.Database.MigrateAsync(ct);

        Assert.Empty(await upgradedContext.Database.GetPendingMigrationsAsync(ct));
        Assert.False(upgradedContext.Database.HasPendingModelChanges());

        var legacyLoaded = await new GetTenantByIdHandler(upgradedContext)
            .HandleAsync(new GetTenantByIdQuery(legacyTenantId), ct);

        Assert.NotNull(legacyLoaded);
        Assert.Equal(legacyPublicId, legacyLoaded.PublicId);
        Assert.Equal(TenantOperationalState.PendienteAdmin, legacyLoaded.OperationalState);
        Assert.Equal("PENDIENTE_ADMIN", legacyLoaded.OperationalStateCode);
        Assert.False(legacyLoaded.MayAuthenticate);
        Assert.Null(legacyLoaded.InitialProvisioning);

        var registerHandler = new RegisterTenantHandler(
            upgradedContext,
            TimeProvider.System,
            NullLogger<RegisterTenantHandler>.Instance);

        var newTenantResult = await registerHandler.HandleAsync(
            new RegisterTenantCommand(
                "clinica-nueva-post-migracion",
                "Clínica Nueva Post Migración",
                "contacto@clinica-nueva.test",
                "Admin Post Migración",
                "admin@clinica-nueva.test",
                MaxActiveAccounts: 25),
            ct);

        Assert.Equal(RegisterTenantStatus.Created, newTenantResult.Status);
        Assert.NotNull(newTenantResult.Tenant?.InitialProvisioning);
    }
}
