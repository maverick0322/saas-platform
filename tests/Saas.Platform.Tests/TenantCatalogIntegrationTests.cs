using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class TenantCatalogIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task InitialMigration_OnEmptyPostgreSqlDatabase_AppliesCleanlyWithNoPendingModelChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var emptyDbConnectionString = await fixture.CreateEmptyDatabaseAsync(
            $"plat01_empty_{Guid.NewGuid():N}",
            ct);

        await using var dbContext = fixture.CreateDbContext(emptyDbConnectionString);

        var pendingBefore = (await dbContext.Database.GetPendingMigrationsAsync(ct)).ToList();
        Assert.Contains("20261122180000_InitialTenantCatalog", pendingBefore);
        Assert.Contains("20261124053749_AddTenantOperationalStateAndProvisioningOperations", pendingBefore);

        await dbContext.Database.MigrateAsync(ct);

        var applied = (await dbContext.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pendingAfter = (await dbContext.Database.GetPendingMigrationsAsync(ct)).ToList();

        Assert.Contains("20261122180000_InitialTenantCatalog", applied);
        Assert.Contains("20261124053749_AddTenantOperationalStateAndProvisioningOperations", applied);
        Assert.Empty(pendingAfter);
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task RegisterTenant_WithValidSyntheticData_PersistsAndCanBeRetrievedInNewDbContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-central-{Guid.NewGuid():N}"[..32];

        RegisterTenantResult registrationResult;
        await using (var writeContext = fixture.CreateDbContext())
        {
            var registerHandler = new RegisterTenantHandler(
                writeContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            registrationResult = await registerHandler.HandleAsync(
                new RegisterTenantCommand(
                    PublicId: publicId.ToUpperInvariant(),
                    Name: "Clínica Sintética Central",
                    ContactEmail: "Contacto@Clinica-Central.Test",
                    InitialAdminFullName: "Dra. Sofía Mendoza",
                    InitialAdminEmail: "Admin.Inicial@Clinica-Central.Test"),
                ct);
        }

        Assert.Equal(RegisterTenantStatus.Created, registrationResult.Status);
        Assert.NotNull(registrationResult.Tenant);
        Assert.Empty(registrationResult.ValidationErrors);
        Assert.Null(registrationResult.ConflictReason);

        var createdTenant = registrationResult.Tenant;
        Assert.NotEqual(Guid.Empty, createdTenant.Id);
        Assert.Equal(publicId, createdTenant.PublicId);
        Assert.Equal("Clínica Sintética Central", createdTenant.Name);
        Assert.Equal("contacto@clinica-central.test", createdTenant.ContactEmail);
        Assert.Equal(TenantOperationalState.PendienteAdmin, createdTenant.OperationalState);
        Assert.Equal("PENDIENTE_ADMIN", createdTenant.OperationalStateCode);
        Assert.False(createdTenant.MayAuthenticate);

        // Recreate DbContext to verify real persistence in PostgreSQL.
        await using var readContext = fixture.CreateDbContext();
        var queryHandler = new GetTenantByIdHandler(readContext);

        var loaded = await queryHandler.HandleAsync(new GetTenantByIdQuery(createdTenant.Id), ct);

        Assert.NotNull(loaded);
        Assert.Equal(createdTenant.Id, loaded.Id);
        Assert.Equal(createdTenant.PublicId, loaded.PublicId);
        Assert.Equal(createdTenant.Name, loaded.Name);
        Assert.Equal(createdTenant.ContactEmail, loaded.ContactEmail);
        Assert.Equal(TenantOperationalState.PendienteAdmin, loaded.OperationalState);
        Assert.NotNull(loaded.InitialProvisioning);
        Assert.Equal(createdTenant.InitialProvisioning!.OperationId, loaded.InitialProvisioning.OperationId);
        Assert.Equal(createdTenant.InitialProvisioning.ProvisioningRequestId, loaded.InitialProvisioning.ProvisioningRequestId);

        Assert.Null(await queryHandler.HandleAsync(new GetTenantByIdQuery(Guid.Empty), ct));
        Assert.Null(await queryHandler.HandleAsync(new GetTenantByIdQuery(Guid.NewGuid()), ct));
    }

    [Fact]
    public async Task RegisterTenant_WithDuplicatePublicId_ReturnsConflictAndKeepsSingleRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-dup-{Guid.NewGuid():N}"[..28];

        await using (var firstContext = fixture.CreateDbContext())
        {
            var handler = new RegisterTenantHandler(
                firstContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            var first = await handler.HandleAsync(
                new RegisterTenantCommand(
                    publicId,
                    "Clínica Sintética Uno",
                    "contacto-uno@clinica.test",
                    "Admin Uno",
                    "admin-uno@clinica.test"),
                ct);
            Assert.Equal(RegisterTenantStatus.Created, first.Status);
        }

        await using (var secondContext = fixture.CreateDbContext())
        {
            var handler = new RegisterTenantHandler(
                secondContext,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            var duplicate = await handler.HandleAsync(
                new RegisterTenantCommand(
                    publicId.ToUpperInvariant(),
                    "Clínica Sintética Dos",
                    "contacto-dos@clinica.test",
                    "Admin Dos",
                    "admin-dos@clinica.test"),
                ct);

            Assert.Equal(RegisterTenantStatus.Conflict, duplicate.Status);
            Assert.Null(duplicate.Tenant);
            Assert.False(string.IsNullOrWhiteSpace(duplicate.ConflictReason));
        }

        await using var verifyContext = fixture.CreateDbContext();
        var count = await verifyContext.Tenants.CountAsync(t => t.PublicId == publicId, ct);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RegisterTenant_ConcurrentDuplicateRegistrations_AllowsOnlyOneAndReturnsConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-race-{Guid.NewGuid():N}"[..29];

        using var barrier = new SemaphoreSlim(0, 2);

        async Task<RegisterTenantResult> RegisterAttemptAsync(string name, string contactEmail, string adminName, string adminEmail)
        {
            await using var context = fixture.CreateDbContext();
            var handler = new RegisterTenantHandler(
                context,
                TimeProvider.System,
                NullLogger<RegisterTenantHandler>.Instance);

            await barrier.WaitAsync(ct);
            return await handler.HandleAsync(
                new RegisterTenantCommand(publicId, name, contactEmail, adminName, adminEmail),
                ct);
        }

        var task1 = RegisterAttemptAsync("Clínica Concurrente A", "contacto-a@clinica.test", "Admin A", "admin-a@clinica.test");
        var task2 = RegisterAttemptAsync("Clínica Concurrente B", "contacto-b@clinica.test", "Admin B", "admin-b@clinica.test");

        barrier.Release(2);
        var results = await Task.WhenAll(task1, task2);

        Assert.Single(results, r => r.Status == RegisterTenantStatus.Created);
        Assert.Single(results, r => r.Status == RegisterTenantStatus.Conflict);

        await using var verifyContext = fixture.CreateDbContext();
        var count = await verifyContext.Tenants.CountAsync(t => t.PublicId == publicId, ct);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Tenant_ConcurrentUpdate_ThrowsDbUpdateConcurrencyException()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicId = $"clinica-conc-{Guid.NewGuid():N}"[..29];

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
                    "Clínica Original",
                    "contacto-orig@clinica.test",
                    "Admin Original",
                    "admin-orig@clinica.test"),
                ct);
            Assert.Equal(RegisterTenantStatus.Created, created.Status);
            tenantId = created.Tenant!.Id;
        }

        await using var contextA = fixture.CreateDbContext();
        await using var contextB = fixture.CreateDbContext();

        var tenantA = await contextA.Tenants.SingleAsync(t => t.Id == tenantId, ct);
        var tenantB = await contextB.Tenants.SingleAsync(t => t.Id == tenantId, ct);

        tenantA.UpdateAdministrativeName("Clínica Actualizada A", TimeProvider.System.GetUtcNow());
        await contextA.SaveChangesAsync(ct);

        tenantB.UpdateAdministrativeName("Clínica Actualizada B", TimeProvider.System.GetUtcNow());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task PostgreSqlCheckConstraints_RejectInvalidDirectInserts()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.tenants (id, public_id, name, contact_email, operational_state, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES (@id, 'x', 'Clínica Inválida', 'admin@clinica.test', 'PENDIENTE_ADMIN', NOW(), NOW(), @stamp);
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("stamp", Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task RegisterTenant_WithInvalidInput_ReturnsValidationFailedWithoutPersisting()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var context = fixture.CreateDbContext();
        var initialTenantCount = await context.Tenants.CountAsync(ct);
        var initialOpCount = await context.TenantProvisioningOperations.CountAsync(ct);

        var handler = new RegisterTenantHandler(
            context,
            TimeProvider.System,
            NullLogger<RegisterTenantHandler>.Instance);

        var result = await handler.HandleAsync(
            new RegisterTenantCommand("a", "", "correo-invalido", "", "admin-invalido"),
            ct);

        Assert.Equal(RegisterTenantStatus.ValidationFailed, result.Status);
        Assert.Null(result.Tenant);
        Assert.Equal(5, result.ValidationErrors.Count);

        var afterTenantCount = await context.Tenants.CountAsync(ct);
        var afterOpCount = await context.TenantProvisioningOperations.CountAsync(ct);
        Assert.Equal(initialTenantCount, afterTenantCount);
        Assert.Equal(initialOpCount, afterOpCount);
    }
}
