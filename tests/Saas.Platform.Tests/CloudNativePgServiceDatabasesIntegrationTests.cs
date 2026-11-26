using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class CloudNativePgServiceDatabasesIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task NpgsqlSqlVerifier_RealPostgres17Tls_EnforcesPgHbaRevokesAclAndDistinguishesErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        const string startupScript = """
            set -euo pipefail
            apk add --no-cache openssl >/dev/null 2>&1
            mkdir -p /tmp/pgssl /tmp/pgdata
            openssl req -new -x509 -days 1 -nodes -subj "/CN=plat05b-it" \
              -out /tmp/pgssl/server.crt -keyout /tmp/pgssl/server.key >/dev/null 2>&1
            chown -R postgres:postgres /tmp/pgssl /tmp/pgdata
            chmod 600 /tmp/pgssl/server.key

            su - postgres -c "initdb -D /tmp/pgdata --auth=trust >/dev/null"
            cat >> /tmp/pgdata/postgresql.conf <<EOF
            listen_addresses = '*'
            port = 5432
            ssl = on
            ssl_cert_file = '/tmp/pgssl/server.crt'
            ssl_key_file = '/tmp/pgssl/server.key'
            password_encryption = 'scram-sha-256'
            EOF

            cat > /tmp/pgdata/pg_hba.conf <<EOF
            local all all trust
            hostssl identity identity_owner all scram-sha-256
            hostssl clinical clinical_owner all scram-sha-256
            host all identity_owner,clinical_owner all reject
            host all all all scram-sha-256
            EOF

            su - postgres -c "pg_ctl -D /tmp/pgdata -l /tmp/pg.log start >/dev/null"
            su - postgres -c "psql -v ON_ERROR_STOP=1 -d postgres" <<SQL
            CREATE ROLE identity_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE clinical_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE DATABASE app;
            CREATE DATABASE identity OWNER identity_owner;
            CREATE DATABASE clinical OWNER clinical_owner;
            SQL
            exec su - postgres -c "pg_ctl -D /tmp/pgdata stop >/dev/null && postgres -D /tmp/pgdata"
            """;

        await using var pgContainer = new ContainerBuilder("postgres:17-alpine")
            .WithPortBinding(5432, true)
            .WithCommand("bash", "-c", startupScript.Replace("\r\n", "\n", StringComparison.Ordinal))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("database system is ready to accept connections"))
            .Build();

        await pgContainer.StartAsync(ct);

        var host = pgContainer.Hostname;
        var port = pgContainer.GetMappedPublicPort(5432);

        var options = CreateEnabledOptions();
        options.ServiceDatabases.PostgreSqlPort = port;
        var verifier = new NpgsqlServiceDatabaseSqlVerifier(
            options,
            NullLogger<NpgsqlServiceDatabaseSqlVerifier>.Instance);

        // 1. Pre-LOGIN pg_hba enforcement (roles siguen NOLOGIN)
        var preLogin = await verifier.VerifyPreLoginPgHbaEnforcementAsync(host, port, ct);
        Assert.Equal(ServiceDatabaseSqlVerificationOutcome.Passed, preLogin.Outcome);

        // 2. Host/puerto inalcanzable debe reportar indisponibilidad, nunca aceptarse como rechazo de pg_hba
        var unreachable = await verifier.VerifyPreLoginPgHbaEnforcementAsync("127.0.0.1", 1, ct);
        Assert.Equal(ServiceDatabaseSqlVerificationOutcome.TransientFailure, unreachable.Outcome);
        Assert.Equal("PG_INFRASTRUCTURE_UNREACHABLE", unreachable.ErrorCode);

        // 3. Activar LOGIN con contraseñas SCRAM independientes (simulando reconciliacion de DatabaseRole)
        const string identityPassword = "IdRealTestPass_05B!";
        const string clinicalPassword = "ClRealTestPass_05B!";
        await pgContainer.ExecAsync(
            [
                "su", "-", "postgres", "-c",
                $"psql -v ON_ERROR_STOP=1 -d postgres -c \"ALTER ROLE identity_owner WITH LOGIN PASSWORD '{identityPassword}'; ALTER ROLE clinical_owner WITH LOGIN PASSWORD '{clinicalPassword}';\""
            ],
            ct);

        // 4. Revocar CONNECT FROM PUBLIC como cada propietario en TLS y validar ACL/privilegios
        var revokeIdentity = await verifier.RevokePublicConnectAndVerifyOwnerSessionAsync(
            host,
            port,
            ServiceDatabaseTarget.Identity,
            ServiceDatabaseTarget.Clinical,
            identityPassword,
            ct);
        Assert.Equal(ServiceDatabaseSqlVerificationOutcome.Passed, revokeIdentity.Outcome);

        var revokeClinical = await verifier.RevokePublicConnectAndVerifyOwnerSessionAsync(
            host,
            port,
            ServiceDatabaseTarget.Clinical,
            ServiceDatabaseTarget.Identity,
            clinicalPassword,
            ct);
        Assert.Equal(ServiceDatabaseSqlVerificationOutcome.Passed, revokeClinical.Outcome);

        // 5. Verificar que un propietario puede operar su propio esquema (y dentro del contenedor escucha en 5432)
        var ownerSchemaResult = await pgContainer.ExecAsync(
            [
                "env", $"PGPASSWORD={identityPassword}",
                "psql", "host=127.0.0.1 port=5432 dbname=identity user=identity_owner sslmode=require",
                "-c", "CREATE TABLE plat05b_schema_probe (id INT PRIMARY KEY); DROP TABLE plat05b_schema_probe;"
            ],
            ct);
        Assert.Equal(0, ownerSchemaResult.ExitCode);

        // 6. Matriz negativa post-LOGIN completa (10 combinaciones rechazadas, incluyendo base 'app' de CNPG)
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServiceDatabaseTarget.Identity.ServiceKey] = identityPassword,
            [ServiceDatabaseTarget.Clinical.ServiceKey] = clinicalPassword
        };
        var negativeMatrix = await verifier.VerifyPostLoginNegativeMatrixAsync(host, port, passwords, ct);
        Assert.Equal(ServiceDatabaseSqlVerificationOutcome.Passed, negativeMatrix.Outcome);

        // 7. Quitar reglas pg_hba restrictivas y comprobar que VerifyPreLogin distingue ausencia de reglas
        await pgContainer.ExecAsync(
            [
                "bash", "-c",
                "cat > /tmp/pgdata/pg_hba.conf <<'EOF'\nlocal all all trust\nhost all all all scram-sha-256\nEOF\nsu - postgres -c 'pg_ctl -D /tmp/pgdata reload >/dev/null'"
            ],
            ct);

        var missingPgHba = await verifier.VerifyPreLoginPgHbaEnforcementAsync(host, port, ct);
        Assert.Equal(ServiceDatabaseSqlVerificationOutcome.TransientFailure, missingPgHba.Outcome);
        Assert.Equal("CNPG_PGHBA_NOT_APPLIED", missingPgHba.ErrorCode);
    }

    [Fact]
    public async Task ProcessNextAsync_AndControlledResume_CompleteServiceDatabasesAndStopAtClinicalPreparation()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("plat05b_resume_flow", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 1, 0, 0, TimeSpan.Zero));
        var (tenantId, operationId, provisioningRequestId) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-plat05b-flow",
            clock,
            ct);

        var clusterName = CloudNativePgClusterSpec.BuildClusterName(tenantId);
        const string clusterUid = "84fb9b6c-b40a-4c85-b822-810907498f73";
        var optionsWithStage2Disabled = CreateEnabledOptions(serviceDatabasesEnabled: false);
        var clusterSnapshot = BuildReadyCluster(tenantId, operationId, clusterUid, optionsWithStage2Disabled);

        // Paso A: Simular estado conservado de PLAT-05A (Infrastructure CONFIRMED, detenido en ServiceDatabases ADAPTER_NOT_CONFIGURED)
        await using (var phaseADb = fixture.CreateDbContext(connectionString))
        {
            var leaseStore = new ProvisioningLeaseStore(phaseADb);
            var executorPhaseA = new CloudNativePgProvisioningStageExecutor(
                new StaticClusterClient(clusterSnapshot),
                optionsWithStage2Disabled,
                clock,
                NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

            var processorPhaseA = new ProvisioningOperationProcessor(
                leaseStore,
                executorPhaseA,
                clock,
                CreateWorkerOptions(),
                NullLogger<ProvisioningOperationProcessor>.Instance);

            var cycleA = await processorPhaseA.ProcessNextAsync("worker-plat05a", ct);
            Assert.Equal(ProvisioningCycleOutcome.AdapterNotConfigured, cycleA.Outcome);
            Assert.Equal(ProvisioningStage.ServiceDatabases, cycleA.LastStage);
            Assert.Equal(1, cycleA.AttemptCount);
        }

        // Paso B: Reanudacion controlada con ServiceDatabases habilitado
        clock.Advance(TimeSpan.FromMinutes(1));
        var optionsStage2Enabled = CreateEnabledOptions(serviceDatabasesEnabled: true);
        var resourcesClient = new InMemoryResourcesClient(optionsStage2Enabled);
        var sqlVerifier = new AlwaysPassSqlVerifier();

        await using (var resumeDb = fixture.CreateDbContext(connectionString))
        {
            var leaseStore = new ProvisioningLeaseStore(resumeDb);
            var stageHandler = new CloudNativePgServiceDatabasesStageHandler(
                new StaticClusterClient(clusterSnapshot),
                resourcesClient,
                sqlVerifier,
                optionsStage2Enabled,
                clock,
                NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);
            var executorPhaseB = new CloudNativePgProvisioningStageExecutor(
                new StaticClusterClient(clusterSnapshot),
                optionsStage2Enabled,
                clock,
                NullLogger<CloudNativePgProvisioningStageExecutor>.Instance,
                stageHandler);

            var resumeHandler = new ResumeProvisioningOperationHandler(
                leaseStore,
                executorPhaseB,
                clock,
                CreateWorkerOptions(),
                NullLogger<ResumeProvisioningOperationHandler>.Instance);

            var resumeResult = await resumeHandler.HandleAsync(operationId, ct);
            Assert.True(resumeResult.Resumed);
            Assert.Equal(ProvisioningStage.ServiceDatabases, resumeResult.NextStage);
            Assert.Equal(1, resumeResult.AttemptCount);

            // Paso C: Adquisicion ordinaria mediante lease y ejecucion completa de ServiceDatabases hasta detenerse en ClinicalPreparation
            var processorPhaseB = new ProvisioningOperationProcessor(
                leaseStore,
                executorPhaseB,
                clock,
                CreateWorkerOptions(),
                NullLogger<ProvisioningOperationProcessor>.Instance);

            var cycleB = await processorPhaseB.ProcessNextAsync("worker-plat05b", ct);
            Assert.Equal(ProvisioningCycleOutcome.AdapterNotConfigured, cycleB.Outcome);
            Assert.Equal(ProvisioningStage.ClinicalPreparation, cycleB.LastStage);
            Assert.Equal(2, cycleB.AttemptCount);
        }

        await using var finalDb = fixture.CreateDbContext(connectionString);
        var persistedOp = await finalDb.TenantProvisioningOperations.AsNoTracking().SingleAsync(op => op.Id == operationId, ct);
        var persistedTenant = await finalDb.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);

        Assert.Equal(provisioningRequestId, persistedOp.ProvisioningRequestId);
        Assert.Equal($"{optionsStage2Enabled.Namespace}/{clusterName}@{clusterUid}", persistedOp.InfrastructureResourceRef);
        Assert.Equal(ProvisioningStageState.Confirmed, persistedOp.InfrastructureStageState);
        Assert.Equal(ProvisioningStageState.Confirmed, persistedOp.ServiceDatabasesStageState);
        Assert.Equal(ProvisioningStageState.Pending, persistedOp.ClinicalStageState);
        Assert.Equal(ProvisioningStageState.Pending, persistedOp.IdentityStageState);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, persistedOp.Status);
        Assert.Equal("ADAPTER_NOT_CONFIGURED", persistedOp.LastErrorCode);
        Assert.True(persistedOp.ManualInterventionRequired);
        Assert.Null(persistedOp.NextAttemptAtUtc);
        Assert.Equal(2, persistedOp.AttemptCount);
        Assert.Equal(TenantOperationalState.PendienteAdmin, persistedTenant.OperationalState);
        Assert.False(persistedTenant.MayAuthenticate);
    }

    [Fact]
    public async Task ResumeProvisioningOperation_EnforcesGuardsAgainstInvalidStateActiveLeaseBudgetUnsupportedStageAndConcurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("plat05b_resume_guards", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 2, 0, 0, TimeSpan.Zero));
        var (tenantId, operationId, _) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-plat05b-guards",
            clock,
            ct);

        var optionsEnabled = CreateEnabledOptions(serviceDatabasesEnabled: true);
        var clusterSnapshot = BuildReadyCluster(tenantId, operationId, "84fb9b6c-b40a-4c85-b822-810907498f73", optionsEnabled);
        var executorSupported = new CloudNativePgProvisioningStageExecutor(
            new StaticClusterClient(clusterSnapshot),
            optionsEnabled,
            clock,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance,
            new CloudNativePgServiceDatabasesStageHandler(
                new StaticClusterClient(clusterSnapshot),
                new InMemoryResourcesClient(optionsEnabled),
                new AlwaysPassSqlVerifier(),
                optionsEnabled,
                clock,
                NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance));

        await using var db = fixture.CreateDbContext(connectionString);
        var store = new ProvisioningLeaseStore(db);

        // 1. Operacion en PENDING (no FAILED_RETRYABLE / ADAPTER_NOT_CONFIGURED) -> rechazada
        var pendingReject = await store.TryResumeAdapterStoppedOperationAsync(
            operationId,
            clock.GetUtcNow(),
            maxTotalAttempts: 4,
            executorSupported.SupportsStage,
            ct);
        Assert.False(pendingReject.Resumed);
        Assert.Equal(ResumeProvisioningOperationOutcome.InvalidStatusOrReason, pendingReject.Outcome);

        // 2. Adquirir lease activo -> rechazada por ActiveLease
        var acquired = await store.TryAcquireNextAsync("worker-busy", clock.GetUtcNow(), TimeSpan.FromMinutes(2), 4, ct);
        Assert.NotNull(acquired);
        var busyReject = await store.TryResumeAdapterStoppedOperationAsync(
            operationId,
            clock.GetUtcNow(),
            maxTotalAttempts: 4,
            executorSupported.SupportsStage,
            ct);
        Assert.False(busyReject.Resumed);
        Assert.Equal(ResumeProvisioningOperationOutcome.ActiveLease, busyReject.Outcome);

        // 3. Confirmar etapa 1 y estacionar con ADAPTER_NOT_CONFIGURED en etapa 2
        Assert.True(await store.TryRecordStageConfirmedAsync(
            acquired.Token,
            ProvisioningStage.InfrastructureInstance,
            $"{optionsEnabled.Namespace}/{CloudNativePgClusterSpec.BuildClusterName(tenantId)}@84fb9b6c-b40a-4c85-b822-810907498f73",
            clock.GetUtcNow(),
            ct));
        Assert.True(await store.TryReleaseWithRecoverableFailureAsync(
            acquired.Token,
            markStageVerificationRequired: null,
            errorCode: "ADAPTER_NOT_CONFIGURED",
            errorSummary: "Waiting for PLAT-05B.",
            utcNow: clock.GetUtcNow(),
            nextAttemptAtUtc: null,
            manualInterventionRequired: true,
            cancellationToken: ct));

        // 4. Etapa siguiente NO soportada (ServiceDatabases=false) -> NextStageAdapterNotConfigured
        var unsupportedReject = await store.TryResumeAdapterStoppedOperationAsync(
            operationId,
            clock.GetUtcNow(),
            maxTotalAttempts: 4,
            stage => stage == ProvisioningStage.InfrastructureInstance,
            ct);
        Assert.False(unsupportedReject.Resumed);
        Assert.Equal(ResumeProvisioningOperationOutcome.NextStageAdapterNotConfigured, unsupportedReject.Outcome);

        // 5. Presupuesto agotado (AttemptCount=1 >= maxTotalAttempts=1) -> AttemptBudgetExhausted
        var budgetReject = await store.TryResumeAdapterStoppedOperationAsync(
            operationId,
            clock.GetUtcNow(),
            maxTotalAttempts: 1,
            executorSupported.SupportsStage,
            ct);
        Assert.False(budgetReject.Resumed);
        Assert.Equal(ResumeProvisioningOperationOutcome.AttemptBudgetExhausted, budgetReject.Outcome);

        // 6. Reanudacion concurrente: exactamente 1 gana y el otro falla limpiamente
        var storeA = new ProvisioningLeaseStore(fixture.CreateDbContext(connectionString));
        var storeB = new ProvisioningLeaseStore(fixture.CreateDbContext(connectionString));
        var results = await Task.WhenAll(
            storeA.TryResumeAdapterStoppedOperationAsync(operationId, clock.GetUtcNow(), 4, executorSupported.SupportsStage, ct),
            storeB.TryResumeAdapterStoppedOperationAsync(operationId, clock.GetUtcNow(), 4, executorSupported.SupportsStage, ct));

        Assert.Equal(1, results.Count(r => r.Resumed));
        Assert.Equal(1, results.Count(r => !r.Resumed && r.Outcome == ResumeProvisioningOperationOutcome.AlreadyResumedOrNoPendingStage));

        // 7. Tenant ya en ACTIVA -> TenantNotPendingAdmin
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenants SET operational_state = 'ACTIVA' WHERE id = {tenantId};",
            ct);
        var activeTenantReject = await store.TryResumeAdapterStoppedOperationAsync(
            operationId,
            clock.GetUtcNow(),
            4,
            executorSupported.SupportsStage,
            ct);
        Assert.False(activeTenantReject.Resumed);
        Assert.Equal(ResumeProvisioningOperationOutcome.TenantNotPendingAdmin, activeTenantReject.Outcome);
    }

    [Fact]
    public async Task ProcessNextAsync_RefusesToConfirmServiceDatabasesWhenGuaranteeFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("plat05b_noguarantee", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero));
        var (tenantId, operationId, _) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-plat05b-fail",
            clock,
            ct);

        var optionsEnabled = CreateEnabledOptions(serviceDatabasesEnabled: true);
        const string clusterUid = "84fb9b6c-b40a-4c85-b822-810907498f73";
        var clusterSnapshot = BuildReadyCluster(tenantId, operationId, clusterUid, optionsEnabled);

        await using var db = fixture.CreateDbContext(connectionString);
        var leaseStore = new ProvisioningLeaseStore(db);

        // Avanzar etapa 1 hasta CONFIRMED en estado estacionado
        var acquired = await leaseStore.TryAcquireNextAsync("worker-s1", clock.GetUtcNow(), TimeSpan.FromMinutes(1), 4, ct);
        Assert.NotNull(acquired);
        Assert.True(await leaseStore.TryRecordStageConfirmedAsync(
            acquired.Token,
            ProvisioningStage.InfrastructureInstance,
            $"{optionsEnabled.Namespace}/{CloudNativePgClusterSpec.BuildClusterName(tenantId)}@{clusterUid}",
            clock.GetUtcNow(),
            ct));
        Assert.True(await leaseStore.TryReleaseWithRecoverableFailureAsync(
            acquired.Token,
            null,
            "ADAPTER_NOT_CONFIGURED",
            "Ready for stage 2",
            clock.GetUtcNow(),
            null,
            manualInterventionRequired: true,
            cancellationToken: ct));

        var failingExecutor = new CloudNativePgProvisioningStageExecutor(
            new StaticClusterClient(clusterSnapshot),
            optionsEnabled,
            clock,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance,
            new CloudNativePgServiceDatabasesStageHandler(
                new StaticClusterClient(clusterSnapshot),
                new InMemoryResourcesClient(optionsEnabled),
                new FailingAclSqlVerifier(),
                optionsEnabled,
                clock,
                NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance));

        var resumeHandler = new ResumeProvisioningOperationHandler(
            leaseStore,
            failingExecutor,
            clock,
            CreateWorkerOptions(),
            NullLogger<ResumeProvisioningOperationHandler>.Instance);
        Assert.True((await resumeHandler.HandleAsync(operationId, ct)).Resumed);

        var processor = new ProvisioningOperationProcessor(
            leaseStore,
            failingExecutor,
            clock,
            CreateWorkerOptions(),
            NullLogger<ProvisioningOperationProcessor>.Instance);

        var result = await processor.ProcessNextAsync("worker-s2-fail", ct);
        Assert.Equal(ProvisioningCycleOutcome.TerminalFailed, result.Outcome);

        var op = await db.TenantProvisioningOperations.AsNoTracking().SingleAsync(x => x.Id == operationId, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId, ct);
        Assert.Equal(ProvisioningOperationStatus.FailedTerminal, op.Status);
        Assert.Equal("PG_ACL_VERIFICATION_FAILED", op.LastErrorCode);
        Assert.Equal(ProvisioningStageState.Confirmed, op.InfrastructureStageState);
        Assert.NotEqual(ProvisioningStageState.Confirmed, op.ServiceDatabasesStageState);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);
    }

    [Fact]
    public async Task ProcessNextAsync_DoesNotConfirmServiceDatabasesWhenAuthSecretMissingForActiveRoleOrDistributionInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("plat05b_fix_guard", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 4, 0, 0, TimeSpan.Zero));
        var (tenantId, operationId, _) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-plat05b-fixguard",
            clock,
            ct);

        var optionsEnabled = CreateEnabledOptions(serviceDatabasesEnabled: true);
        const string clusterUid = "84fb9b6c-b40a-4c85-b822-810907498f73";
        var clusterSnapshot = BuildReadyCluster(tenantId, operationId, clusterUid, optionsEnabled);

        await using var db = fixture.CreateDbContext(connectionString);
        var leaseStore = new ProvisioningLeaseStore(db);

        var acquired = await leaseStore.TryAcquireNextAsync("worker-s1-guard", clock.GetUtcNow(), TimeSpan.FromMinutes(1), 4, ct);
        Assert.NotNull(acquired);
        Assert.True(await leaseStore.TryRecordStageConfirmedAsync(
            acquired.Token,
            ProvisioningStage.InfrastructureInstance,
            $"{optionsEnabled.Namespace}/{CloudNativePgClusterSpec.BuildClusterName(tenantId)}@{clusterUid}",
            clock.GetUtcNow(),
            ct));
        Assert.True(await leaseStore.TryReleaseWithRecoverableFailureAsync(
            acquired.Token,
            null,
            "ADAPTER_NOT_CONFIGURED",
            "Ready for stage 2",
            clock.GetUtcNow(),
            null,
            manualInterventionRequired: true,
            cancellationToken: ct));

        var resourcesClient = new InMemoryResourcesClient(optionsEnabled);
        var idRoleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(tenantId);
        await resourcesClient.CreateDatabaseRoleAsync(
            optionsEnabled.Namespace,
            ServiceDatabaseTarget.BuildDatabaseRoleManifest(
                new ProvisioningStageContext(
                    operationId,
                    tenantId,
                    "cln-plat05b-fixguard",
                    "Clinica Guard",
                    "req-guard",
                    "Admin",
                    "admin@synthetic.test",
                    ProvisioningStage.ServiceDatabases,
                    false,
                    2,
                    $"{optionsEnabled.Namespace}/{CloudNativePgClusterSpec.BuildClusterName(tenantId)}@{clusterUid}",
                    null,
                    null),
                optionsEnabled,
                ServiceDatabaseTarget.Identity,
                CloudNativePgClusterSpec.BuildClusterName(tenantId),
                loginEnabled: true,
                passwordSecretName: ServiceDatabaseTarget.Identity.BuildAuthSecretName(tenantId)),
            ct);

        var executor = new CloudNativePgProvisioningStageExecutor(
            new StaticClusterClient(clusterSnapshot),
            optionsEnabled,
            clock,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance,
            new CloudNativePgServiceDatabasesStageHandler(
                new StaticClusterClient(clusterSnapshot),
                resourcesClient,
                new AlwaysPassSqlVerifier(),
                optionsEnabled,
                clock,
                NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance));

        var resumeHandler = new ResumeProvisioningOperationHandler(
            leaseStore,
            executor,
            clock,
            CreateWorkerOptions(),
            NullLogger<ResumeProvisioningOperationHandler>.Instance);
        Assert.True((await resumeHandler.HandleAsync(operationId, ct)).Resumed);

        var processor = new ProvisioningOperationProcessor(
            leaseStore,
            executor,
            clock,
            CreateWorkerOptions(),
            NullLogger<ProvisioningOperationProcessor>.Instance);

        var result = await processor.ProcessNextAsync("worker-s2-guard", ct);
        Assert.Equal(ProvisioningCycleOutcome.TerminalFailed, result.Outcome);

        var op = await db.TenantProvisioningOperations.AsNoTracking().SingleAsync(x => x.Id == operationId, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
        Assert.Equal(ProvisioningOperationStatus.FailedTerminal, op.Status);
        Assert.Equal("CNPG_AUTH_SECRET_CONFLICT", op.LastErrorCode);
        Assert.Equal(ProvisioningStageState.Confirmed, op.InfrastructureStageState);
        Assert.NotEqual(ProvisioningStageState.Confirmed, op.ServiceDatabasesStageState);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);

        // No debe haber regenerado el auth Secret ni escrito en el Secret de distribucion
        var authGet = await resourcesClient.GetSecretAsync(
            optionsEnabled.Namespace,
            ServiceDatabaseTarget.Identity.BuildAuthSecretName(tenantId),
            ct);
        Assert.Equal(KubernetesResourceCallStatus.NotFound, authGet.Status);
    }

    [Fact]
    public async Task RecoverPlat05bSerialization_RecoversAuthorizedLabOperationAllowsLeaseContinuationAndReusesIdentityRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("plat05b_fix2_recover", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 5, 0, 0, TimeSpan.Zero));
        var provisioningRequestId = "req-plat05b-fix2-lab";

        await SeedAuthorizedPlat05bIncidentAsync(
            connectionString,
            Plat05bSerializationIncidentContract.AuthorizedTenantId,
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            provisioningRequestId,
            clock.GetUtcNow(),
            ct);

        var optionsEnabled = CreateEnabledOptions(serviceDatabasesEnabled: true);
        var clusterSnapshot = BuildReadyCluster(
            Plat05bSerializationIncidentContract.AuthorizedTenantId,
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            "84fb9b6c-b40a-4c85-b822-810907498f73",
            optionsEnabled);

        var resourcesClient = new InMemoryResourcesClient(optionsEnabled);

        // Pre-seed de DatabaseRole existente de Identity tal como lo serializa CNPG v1.30.2 (6 booleanos omitidos)
        var identityRoleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(Plat05bSerializationIncidentContract.AuthorizedTenantId);
        var existingIdentityRoleJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>($$"""
            {
              "apiVersion": "postgresql.cnpg.io/v1",
              "kind": "DatabaseRole",
              "metadata": {
                "name": "{{identityRoleName}}",
                "namespace": "{{optionsEnabled.Namespace}}",
                "uid": "eee4ce4c-f650-471a-8e59-01cc13ac0be9",
                "resourceVersion": "101",
                "generation": 1,
                "labels": {
                  "{{CloudNativePgClusterSpec.ManagedByLabelKey}}": "{{CloudNativePgClusterSpec.ManagedByLabelValue}}",
                  "{{CloudNativePgClusterSpec.TenantIdLabelKey}}": "{{Plat05bSerializationIncidentContract.AuthorizedTenantId:D}}",
                  "{{CloudNativePgClusterSpec.OperationIdLabelKey}}": "{{Plat05bSerializationIncidentContract.AuthorizedOperationId:D}}",
                  "{{ServiceDatabaseTarget.ServiceLabelKey}}": "{{ServiceDatabaseTarget.Identity.ServiceKey}}"
                }
              },
              "spec": {
                "cluster": { "name": "{{CloudNativePgClusterSpec.BuildClusterName(Plat05bSerializationIncidentContract.AuthorizedTenantId)}}" },
                "name": "identity_owner",
                "ensure": "present",
                "databaseRoleReclaimPolicy": "retain"
              },
              "status": {
                "applied": true,
                "observedGeneration": 1
              }
            }
            """);
        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(existingIdentityRoleJson, out var preExistingIdentityRole));
        resourcesClient.SeedRole(preExistingIdentityRole!);

        await using var db = fixture.CreateDbContext(connectionString);
        var leaseStore = new ProvisioningLeaseStore(db);
        var executor = new CloudNativePgProvisioningStageExecutor(
            new StaticClusterClient(clusterSnapshot),
            optionsEnabled,
            clock,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance,
            new CloudNativePgServiceDatabasesStageHandler(
                new StaticClusterClient(clusterSnapshot),
                resourcesClient,
                new AlwaysPassSqlVerifier(),
                optionsEnabled,
                clock,
                NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance));

        // 1. La reanudacion ordinaria (--mode=resume-operation) DEBE seguir rechazando FAILED_TERMINAL
        var ordinaryResume = new ResumeProvisioningOperationHandler(
            leaseStore,
            executor,
            clock,
            CreateWorkerOptions(),
            NullLogger<ResumeProvisioningOperationHandler>.Instance);
        var rejectedOrdinaryResume = await ordinaryResume.HandleAsync(
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            ct);
        Assert.False(rejectedOrdinaryResume.Resumed);
        Assert.Equal(ResumeProvisioningOperationOutcome.InvalidStatusOrReason, rejectedOrdinaryResume.Outcome);

        // 2. Recuperacion excepcional habilita la operacion sin avanzar attempt_count ni lease_version ni ejecutar etapas
        clock.Advance(TimeSpan.FromMinutes(1));
        var recoveryHandler = new RecoverPlat05bSerializationIncidentHandler(
            leaseStore,
            executor,
            clock,
            CreateWorkerOptions(),
            NullLogger<RecoverPlat05bSerializationIncidentHandler>.Instance);
        var recoveryResult = await recoveryHandler.HandleAsync(
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            ct);
        Assert.True(recoveryResult.Recovered);
        Assert.Equal(RecoverPlat05bSerializationOutcome.Recovered, recoveryResult.Outcome);
        Assert.Equal(Plat05bSerializationIncidentContract.AuthorizedTenantId, recoveryResult.TenantId);
        Assert.Equal(2, recoveryResult.AttemptCount);

        var recoveredOp = await db.TenantProvisioningOperations.AsNoTracking()
            .SingleAsync(x => x.Id == Plat05bSerializationIncidentContract.AuthorizedOperationId, ct);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, recoveredOp.Status);
        Assert.False(recoveredOp.ManualInterventionRequired);
        Assert.Equal(clock.GetUtcNow(), recoveredOp.NextAttemptAtUtc);
        Assert.Null(recoveredOp.LastErrorCode);
        Assert.Null(recoveredOp.LastErrorSummary);
        Assert.Equal(2, recoveredOp.AttemptCount);
        Assert.Equal(7L, recoveredOp.LeaseVersion);
        Assert.Equal(provisioningRequestId, recoveredOp.ProvisioningRequestId);
        Assert.Equal(Plat05bSerializationIncidentContract.AuthorizedTenantId, recoveredOp.TenantId);
        Assert.Equal(ProvisioningStageState.Confirmed, recoveredOp.InfrastructureStageState);
        Assert.Equal(Plat05bSerializationIncidentContract.AuthorizedInfrastructureResourceRef, recoveredOp.InfrastructureResourceRef);

        // 3. El Worker ordinario adquiere por lease, reutiliza el DatabaseRole existente de Identity sin recrearlo y completa ServiceDatabases
        var processor = new ProvisioningOperationProcessor(
            leaseStore,
            executor,
            clock,
            CreateWorkerOptions(),
            NullLogger<ProvisioningOperationProcessor>.Instance);
        var workerCycle = await processor.ProcessNextAsync("worker-plat05b-fix2", ct);
        Assert.Equal(ProvisioningCycleOutcome.AdapterNotConfigured, workerCycle.Outcome);
        Assert.Equal(ProvisioningStage.ClinicalPreparation, workerCycle.LastStage);
        Assert.Equal(3, workerCycle.AttemptCount);

        Assert.Equal(
            [ServiceDatabaseTarget.Clinical.BuildRoleResourceName(Plat05bSerializationIncidentContract.AuthorizedTenantId)],
            resourcesClient.CreatedRoleNames);

        await using var finalDb = fixture.CreateDbContext(connectionString);
        var finalOp = await finalDb.TenantProvisioningOperations.AsNoTracking()
            .SingleAsync(x => x.Id == Plat05bSerializationIncidentContract.AuthorizedOperationId, ct);
        var finalTenant = await finalDb.Tenants.AsNoTracking()
            .SingleAsync(t => t.Id == Plat05bSerializationIncidentContract.AuthorizedTenantId, ct);

        Assert.Equal(ProvisioningStageState.Confirmed, finalOp.InfrastructureStageState);
        Assert.Equal(ProvisioningStageState.Confirmed, finalOp.ServiceDatabasesStageState);
        Assert.Equal(ProvisioningStageState.Pending, finalOp.ClinicalStageState);
        Assert.Equal(ProvisioningStageState.Pending, finalOp.IdentityStageState);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, finalOp.Status);
        Assert.Equal("ADAPTER_NOT_CONFIGURED", finalOp.LastErrorCode);
        Assert.True(finalOp.ManualInterventionRequired);
        Assert.Null(finalOp.NextAttemptAtUtc);
        Assert.Equal(3, finalOp.AttemptCount);
        Assert.Equal(8L, finalOp.LeaseVersion);
        Assert.Equal(TenantOperationalState.PendienteAdmin, finalTenant.OperationalState);
        Assert.False(finalTenant.MayAuthenticate);
    }

    [Fact]
    public async Task RecoverPlat05bSerialization_EnforcesStrictWhitelistGuardsBudgetAndConcurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("plat05b_fix2_guards", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 6, 0, 0, TimeSpan.Zero));
        await SeedAuthorizedPlat05bIncidentAsync(
            connectionString,
            Plat05bSerializationIncidentContract.AuthorizedTenantId,
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            "req-plat05b-fix2-guards",
            clock.GetUtcNow(),
            ct);

        await using var db = fixture.CreateDbContext(connectionString);
        var store = new ProvisioningLeaseStore(db);

        // 1. OperationId distinto al autorizado
        var wrongOp = await store.TryRecoverPlat05bSerializationIncidentAsync(
            Guid.NewGuid(),
            clock.GetUtcNow(),
            maxTotalAttempts: 4,
            supportsServiceDatabases: true,
            ct);
        Assert.False(wrongOp.Recovered);
        Assert.Equal(RecoverPlat05bSerializationOutcome.UnauthorizedOperation, wrongOp.Outcome);

        // 2. ServiceDatabases no soportado en configuracion
        var unsupportedAdapter = await store.TryRecoverPlat05bSerializationIncidentAsync(
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            clock.GetUtcNow(),
            maxTotalAttempts: 4,
            supportsServiceDatabases: false,
            ct);
        Assert.False(unsupportedAdapter.Recovered);
        Assert.Equal(RecoverPlat05bSerializationOutcome.NextStageAdapterNotConfigured, unsupportedAdapter.Outcome);

        // 3. Presupuesto de intentos agotado (attempt_count=2 >= maxTotalAttempts=2)
        var exhaustedBudget = await store.TryRecoverPlat05bSerializationIncidentAsync(
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            clock.GetUtcNow(),
            maxTotalAttempts: 2,
            supportsServiceDatabases: true,
            ct);
        Assert.False(exhaustedBudget.Recovered);
        Assert.Equal(RecoverPlat05bSerializationOutcome.AttemptBudgetExhausted, exhaustedBudget.Outcome);

        // 4. Error summary / error code / attempt_count / infra ref distintos al incidente autorizado
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenant_provisioning_operations SET last_error_summary = 'Different summary' WHERE id = {Plat05bSerializationIncidentContract.AuthorizedOperationId};",
            ct);
        Assert.Equal(
            RecoverPlat05bSerializationOutcome.InvalidStatusOrReason,
            (await store.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct)).Outcome);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenant_provisioning_operations SET last_error_summary = {Plat05bSerializationIncidentContract.ExpectedErrorSummary}, last_error_code = 'CNPG_CLUSTER_OWNERSHIP_CONFLICT' WHERE id = {Plat05bSerializationIncidentContract.AuthorizedOperationId};",
            ct);
        Assert.Equal(
            RecoverPlat05bSerializationOutcome.InvalidStatusOrReason,
            (await store.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct)).Outcome);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenant_provisioning_operations SET last_error_code = {Plat05bSerializationIncidentContract.ExpectedErrorCode}, attempt_count = 1 WHERE id = {Plat05bSerializationIncidentContract.AuthorizedOperationId};",
            ct);
        Assert.Equal(
            RecoverPlat05bSerializationOutcome.InvalidStatusOrReason,
            (await store.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct)).Outcome);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenant_provisioning_operations SET attempt_count = 2, infrastructure_resource_ref = 'saas-tenant-databases/other@uid' WHERE id = {Plat05bSerializationIncidentContract.AuthorizedOperationId};",
            ct);
        Assert.Equal(
            RecoverPlat05bSerializationOutcome.InvalidStageStateOrReference,
            (await store.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct)).Outcome);

        // 5. Lease activo / estado IN_PROGRESS
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenant_provisioning_operations SET infrastructure_resource_ref = {Plat05bSerializationIncidentContract.AuthorizedInfrastructureResourceRef}, status = 'IN_PROGRESS', lease_owner = 'worker-x', lease_until_utc = {clock.GetUtcNow().AddMinutes(1)} WHERE id = {Plat05bSerializationIncidentContract.AuthorizedOperationId};",
            ct);
        Assert.Equal(
            RecoverPlat05bSerializationOutcome.ActiveLease,
            (await store.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct)).Outcome);

        // 6. Estado distinto de FAILED_TERMINAL (ej. FAILED_RETRYABLE)
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenant_provisioning_operations SET status = 'FAILED_RETRYABLE', lease_owner = NULL, lease_until_utc = NULL WHERE id = {Plat05bSerializationIncidentContract.AuthorizedOperationId};",
            ct);
        Assert.Equal(
            RecoverPlat05bSerializationOutcome.InvalidStatusOrReason,
            (await store.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct)).Outcome);

        // Restaurar estado exacto del incidente y comprobar concurrencia: solo 1 ejecucion gana
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.tenant_provisioning_operations SET status = 'FAILED_TERMINAL', manual_intervention_required = TRUE, next_attempt_at_utc = NULL WHERE id = {Plat05bSerializationIncidentContract.AuthorizedOperationId};",
            ct);

        var storeA = new ProvisioningLeaseStore(fixture.CreateDbContext(connectionString));
        var storeB = new ProvisioningLeaseStore(fixture.CreateDbContext(connectionString));
        var concurrentResults = await Task.WhenAll(
            storeA.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct),
            storeB.TryRecoverPlat05bSerializationIncidentAsync(Plat05bSerializationIncidentContract.AuthorizedOperationId, clock.GetUtcNow(), 4, true, ct));

        Assert.Equal(1, concurrentResults.Count(r => r.Recovered));
        Assert.Equal(1, concurrentResults.Count(r => !r.Recovered && r.Outcome == RecoverPlat05bSerializationOutcome.InvalidStatusOrReason));

        // 7. TenantId distinto aunque OperationId coincida -> UnauthorizedOperation
        var wrongTenantDb = await CreateMigratedIsolatedDatabaseAsync("plat05b_fix2_wrongtenant", ct);
        await SeedAuthorizedPlat05bIncidentAsync(
            wrongTenantDb,
            Guid.NewGuid(),
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            "req-plat05b-fix2-wrong-tenant",
            clock.GetUtcNow(),
            ct);
        await using var wrongTenantCtx = fixture.CreateDbContext(wrongTenantDb);
        var wrongTenantStore = new ProvisioningLeaseStore(wrongTenantCtx);
        var wrongTenantResult = await wrongTenantStore.TryRecoverPlat05bSerializationIncidentAsync(
            Plat05bSerializationIncidentContract.AuthorizedOperationId,
            clock.GetUtcNow(),
            maxTotalAttempts: 4,
            supportsServiceDatabases: true,
            ct);
        Assert.False(wrongTenantResult.Recovered);
        Assert.Equal(RecoverPlat05bSerializationOutcome.UnauthorizedOperation, wrongTenantResult.Outcome);
    }

    private async Task SeedAuthorizedPlat05bIncidentAsync(
        string connectionString,
        Guid tenantId,
        Guid operationId,
        string provisioningRequestId,
        DateTimeOffset timestampUtc,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        const string insertTenantSql = """
            INSERT INTO platform.tenants (
                id, public_id, name, contact_email, max_active_accounts,
                operational_state, created_at_utc, updated_at_utc, concurrency_stamp)
            VALUES (
                @id, @publicId, @name, @contactEmail, 10,
                'PENDIENTE_ADMIN', @ts, @ts, @stamp);
            """;

        await using (var cmd = new NpgsqlCommand(insertTenantSql, conn))
        {
            cmd.Parameters.AddWithValue("id", tenantId);
            cmd.Parameters.AddWithValue("publicId", $"lab-{tenantId:N}"[..24]);
            cmd.Parameters.AddWithValue("name", "Clinica Lab Sintetica");
            cmd.Parameters.AddWithValue("contactEmail", "lab@synthetic.test");
            cmd.Parameters.AddWithValue("ts", timestampUtc);
            cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        const string insertOpSql = """
            INSERT INTO platform.tenant_provisioning_operations (
                id, tenant_id, provisioning_request_id, initial_admin_full_name, initial_admin_email,
                status, attempt_count, last_error_code, last_error_summary,
                created_at_utc, updated_at_utc, last_attempt_at_utc, completed_at_utc, concurrency_stamp,
                lease_owner, lease_until_utc, lease_version, next_attempt_at_utc, manual_intervention_required,
                infrastructure_stage_state, service_databases_stage_state, clinical_stage_state, identity_stage_state,
                infrastructure_resource_ref, clinical_migration_version, initial_admin_account_id)
            VALUES (
                @opId, @tenantId, @reqId, 'Admin Sintetico', 'admin@synthetic.test',
                'FAILED_TERMINAL', 2, @errCode, @errSummary,
                @ts, @ts, @ts, NULL, @stamp,
                NULL, NULL, 7, NULL, TRUE,
                'CONFIRMED', 'PENDING', 'PENDING', 'PENDING',
                @infraRef, NULL, NULL);
            """;

        await using (var cmd = new NpgsqlCommand(insertOpSql, conn))
        {
            cmd.Parameters.AddWithValue("opId", operationId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("reqId", provisioningRequestId);
            cmd.Parameters.AddWithValue("errCode", Plat05bSerializationIncidentContract.ExpectedErrorCode);
            cmd.Parameters.AddWithValue("errSummary", Plat05bSerializationIncidentContract.ExpectedErrorSummary);
            cmd.Parameters.AddWithValue("ts", timestampUtc);
            cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
            cmd.Parameters.AddWithValue("infraRef", Plat05bSerializationIncidentContract.AuthorizedInfrastructureResourceRef);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<string> CreateMigratedIsolatedDatabaseAsync(string prefix, CancellationToken ct)
    {
        var dbName = $"{prefix}_{Guid.NewGuid():N}"[..36];
        var connectionString = await fixture.CreateEmptyDatabaseAsync(dbName, ct);

        await using var db = fixture.CreateDbContext(connectionString);
        await db.Database.MigrateAsync(ct);
        return connectionString;
    }

    private async Task<(Guid TenantId, Guid OperationId, string ProvisioningRequestId)> RegisterSyntheticTenantAsync(
        string connectionString,
        string publicId,
        TimeProvider clock,
        CancellationToken ct)
    {
        await using var db = fixture.CreateDbContext(connectionString);
        var handler = new RegisterTenantHandler(db, clock, NullLogger<RegisterTenantHandler>.Instance);
        var result = await handler.HandleAsync(
            new RegisterTenantCommand(
                publicId,
                $"Clinica {publicId}",
                $"{publicId}@synthetic.test",
                "Admin Sintetico",
                $"admin.{publicId}@synthetic.test",
                MaxActiveAccounts: 10),
            ct);

        Assert.Equal(RegisterTenantStatus.Created, result.Status);
        Assert.NotNull(result.Tenant?.InitialProvisioning);
        return (result.Tenant.Id, result.Tenant.InitialProvisioning.OperationId, result.Tenant.InitialProvisioning.ProvisioningRequestId);
    }

    private static CloudNativePgOptions CreateEnabledOptions(bool serviceDatabasesEnabled = true)
        => new()
        {
            ApiCallTimeoutSeconds = 2,
            ReadinessTimeoutSeconds = 5,
            ReadinessPollIntervalSeconds = 1,
            ServiceDatabases = new CloudNativePgServiceDatabasesOptions
            {
                Enabled = serviceDatabasesEnabled,
                ServiceDnsSuffix = "svc",
                PostgreSqlPort = 5432,
                SqlConnectTimeoutSeconds = 3,
                SqlCommandTimeoutSeconds = 5,
                SecretDistributionMaxConcurrencyRetries = 3,
                IdentityDistributionSecret = new ServiceDistributionSecretOptions
                {
                    Namespace = "saas-vital",
                    Name = "identity-tenant-connections"
                },
                ClinicalDistributionSecret = new ServiceDistributionSecretOptions
                {
                    Namespace = "saas-vital",
                    Name = "clinical-tenant-connections"
                }
            }
        };

    private static ProvisioningWorkerOptions CreateWorkerOptions()
        => new()
        {
            Enabled = true,
            Mode = ProvisioningExecutionMode.CloudNativePg,
            MaxAutomaticRetries = 3,
            LeaseDurationSeconds = 30,
            InitialRetryDelaySeconds = 5,
            MaxRetryDelaySeconds = 60,
            PollIntervalSeconds = 1
        };

    private static CloudNativePgClusterSnapshot BuildReadyCluster(
        Guid tenantId,
        Guid operationId,
        string uid,
        CloudNativePgOptions options)
        => new(
            CloudNativePgClusterSpec.BuildClusterName(tenantId),
            options.Namespace,
            uid,
            1,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CloudNativePgClusterSpec.ManagedByLabelKey] = CloudNativePgClusterSpec.ManagedByLabelValue,
                [CloudNativePgClusterSpec.TenantIdLabelKey] = tenantId.ToString("D"),
                [CloudNativePgClusterSpec.OperationIdLabelKey] = operationId.ToString("D")
            },
            options.Instances,
            options.ImageName,
            options.StorageClass,
            options.StorageSize,
            options.CpuRequest,
            options.CpuLimit,
            options.MemoryRequest,
            options.MemoryLimit,
            1,
            1,
            $"{CloudNativePgClusterSpec.BuildClusterName(tenantId)}-1",
            $"{CloudNativePgClusterSpec.BuildClusterName(tenantId)}-1",
            "Cluster in healthy state",
            "True",
            "ClusterIsReady",
            1,
            CloudNativePgClusterSpec.RequiredServicePgHbaRules,
            $"{CloudNativePgClusterSpec.BuildClusterName(tenantId)}-rw");

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class StaticClusterClient(CloudNativePgClusterSnapshot cluster) : ICloudNativePgClusterClient
    {
        public void EnsureInitialized() { }
        public Task<CloudNativePgGetResult> GetClusterAsync(string @namespace, string clusterName, CancellationToken cancellationToken)
            => Task.FromResult(CloudNativePgGetResult.FoundCluster(cluster));
        public Task<CloudNativePgCreateResult> CreateClusterAsync(string @namespace, IReadOnlyDictionary<string, object?> manifest, CancellationToken cancellationToken)
            => Task.FromResult(CloudNativePgCreateResult.CreatedCluster(cluster));
    }

    private sealed class AlwaysPassSqlVerifier : IServiceDatabaseSqlVerifier
    {
        public Task<ServiceDatabaseSqlVerificationResult> VerifyPreLoginPgHbaEnforcementAsync(string writeHost, int port, CancellationToken cancellationToken)
            => Task.FromResult(ServiceDatabaseSqlVerificationResult.Passed());
        public Task<ServiceDatabaseSqlVerificationResult> RevokePublicConnectAndVerifyOwnerSessionAsync(string writeHost, int port, ServiceDatabaseTarget target, ServiceDatabaseTarget otherTarget, string password, CancellationToken cancellationToken)
            => Task.FromResult(ServiceDatabaseSqlVerificationResult.Passed());
        public Task<ServiceDatabaseSqlVerificationResult> VerifyPostLoginNegativeMatrixAsync(string writeHost, int port, IReadOnlyDictionary<string, string> servicePasswords, CancellationToken cancellationToken)
            => Task.FromResult(ServiceDatabaseSqlVerificationResult.Passed());
    }

    private sealed class FailingAclSqlVerifier : IServiceDatabaseSqlVerifier
    {
        public Task<ServiceDatabaseSqlVerificationResult> VerifyPreLoginPgHbaEnforcementAsync(string writeHost, int port, CancellationToken cancellationToken)
            => Task.FromResult(ServiceDatabaseSqlVerificationResult.Passed());
        public Task<ServiceDatabaseSqlVerificationResult> RevokePublicConnectAndVerifyOwnerSessionAsync(string writeHost, int port, ServiceDatabaseTarget target, ServiceDatabaseTarget otherTarget, string password, CancellationToken cancellationToken)
            => Task.FromResult(ServiceDatabaseSqlVerificationResult.Terminal("PG_ACL_VERIFICATION_FAILED", "PUBLIC CONNECT remained granted."));
        public Task<ServiceDatabaseSqlVerificationResult> VerifyPostLoginNegativeMatrixAsync(string writeHost, int port, IReadOnlyDictionary<string, string> servicePasswords, CancellationToken cancellationToken)
            => Task.FromResult(ServiceDatabaseSqlVerificationResult.Passed());
    }

    private sealed class InMemoryResourcesClient : ICloudNativePgServiceDatabaseResourcesClient
    {
        private readonly Dictionary<(string, string), CloudNativePgDatabaseRoleSnapshot> _roles = new();
        private readonly Dictionary<(string, string), CloudNativePgDatabaseSnapshot> _databases = new();
        private readonly Dictionary<(string, string), (Dictionary<string, string> Labels, Dictionary<string, string> Data, string Type)> _secrets = new();

        public List<string> CreatedRoleNames { get; } = [];

        public void SeedRole(CloudNativePgDatabaseRoleSnapshot role)
            => _roles[(role.Namespace, role.Name)] = role;

        public InMemoryResourcesClient(CloudNativePgOptions options)
        {
            _secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)] =
                (CreateDistributionLabels(ServiceDatabaseTarget.Identity), new Dictionary<string, string>(StringComparer.Ordinal), ServiceDatabaseTarget.OpaqueSecretType);
            _secrets[(options.ServiceDatabases.ClinicalDistributionSecret.Namespace, options.ServiceDatabases.ClinicalDistributionSecret.Name)] =
                (CreateDistributionLabels(ServiceDatabaseTarget.Clinical), new Dictionary<string, string>(StringComparer.Ordinal), ServiceDatabaseTarget.OpaqueSecretType);
        }

        private static Dictionary<string, string> CreateDistributionLabels(ServiceDatabaseTarget target)
            => new(StringComparer.Ordinal)
            {
                [CloudNativePgClusterSpec.ManagedByLabelKey] = CloudNativePgClusterSpec.ManagedByLabelValue,
                [ServiceDatabaseTarget.PurposeLabelKey] = ServiceDatabaseTarget.DistributionPurposeLabelValue,
                [ServiceDatabaseTarget.ServiceLabelKey] = target.ServiceKey
            };

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> GetDatabaseRoleAsync(string @namespace, string name, CancellationToken cancellationToken)
            => Task.FromResult(_roles.TryGetValue((@namespace, name), out var role)
                ? new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.Found, role)
                : new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.NotFound));

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> CreateDatabaseRoleAsync(string @namespace, IReadOnlyDictionary<string, object?> manifest, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(manifest);
            CloudNativePgDatabaseRoleSnapshot.TryParse(json, out var parsed);
            var snapshot = parsed! with { Uid = $"{parsed.Name}-uid", ResourceVersion = "1", Generation = 1, Applied = true, ObservedGeneration = 1 };
            CreatedRoleNames.Add(snapshot.Name);
            _roles[(@namespace, snapshot.Name)] = snapshot;
            return Task.FromResult(new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.Created, snapshot));
        }

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> ReplaceDatabaseRoleAsync(string @namespace, string name, IReadOnlyDictionary<string, object?> manifest, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(manifest);
            CloudNativePgDatabaseRoleSnapshot.TryParse(json, out var parsed);
            var existing = _roles[(@namespace, name)];
            var nextGen = (existing.Generation ?? 1) + 1;
            var snapshot = parsed! with { Uid = existing.Uid ?? $"{parsed.Name}-uid", ResourceVersion = nextGen.ToString(), Generation = nextGen, Applied = true, ObservedGeneration = nextGen };
            _roles[(@namespace, name)] = snapshot;
            return Task.FromResult(new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.Replaced, snapshot));
        }

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> GetDatabaseAsync(string @namespace, string name, CancellationToken cancellationToken)
            => Task.FromResult(_databases.TryGetValue((@namespace, name), out var db)
                ? new KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>(KubernetesResourceCallStatus.Found, db)
                : new KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>(KubernetesResourceCallStatus.NotFound));

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> CreateDatabaseAsync(string @namespace, IReadOnlyDictionary<string, object?> manifest, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(manifest);
            CloudNativePgDatabaseSnapshot.TryParse(json, out var parsed);
            var snapshot = parsed! with { Uid = $"{parsed.Name}-uid", ResourceVersion = "1", Generation = 1, Applied = true, ObservedGeneration = 1 };
            _databases[(@namespace, snapshot.Name)] = snapshot;
            return Task.FromResult(new KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>(KubernetesResourceCallStatus.Created, snapshot));
        }

        public Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> GetSecretAsync(string @namespace, string name, CancellationToken cancellationToken)
            => Task.FromResult(_secrets.TryGetValue((@namespace, name), out var s)
                ? new KubernetesResourceCallResult<KubernetesSecretSnapshot>(
                    KubernetesResourceCallStatus.Found,
                    new KubernetesSecretSnapshot(name, @namespace, $"{name}-uid", "1", s.Type, null, s.Labels, new Dictionary<string, string>(), new Dictionary<string, string>(s.Data)))
                : new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.NotFound));

        public Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> CreateBasicAuthSecretAsync(string @namespace, string name, IReadOnlyDictionary<string, string> labels, string username, string password, CancellationToken cancellationToken)
        {
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServiceDatabaseTarget.AuthSecretUsernameKey] = username,
                [ServiceDatabaseTarget.AuthSecretPasswordKey] = password
            };
            _secrets[(@namespace, name)] = (new Dictionary<string, string>(labels, StringComparer.Ordinal), data, ServiceDatabaseTarget.BasicAuthSecretType);
            return Task.FromResult(new KubernetesResourceCallResult<KubernetesSecretSnapshot>(
                KubernetesResourceCallStatus.Created,
                new KubernetesSecretSnapshot(name, @namespace, $"{name}-uid", "1", ServiceDatabaseTarget.BasicAuthSecretType, null, labels, new Dictionary<string, string>(), data)));
        }

        public Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> ReplaceSecretDataAsync(string @namespace, string name, KubernetesSecretSnapshot existingSnapshot, IReadOnlyDictionary<string, string> updatedData, CancellationToken cancellationToken)
        {
            var existing = _secrets[(@namespace, name)];
            _secrets[(@namespace, name)] = (existing.Labels, new Dictionary<string, string>(updatedData, StringComparer.Ordinal), existing.Type);
            return Task.FromResult(new KubernetesResourceCallResult<KubernetesSecretSnapshot>(
                KubernetesResourceCallStatus.Replaced,
                new KubernetesSecretSnapshot(name, @namespace, $"{name}-uid", "2", existing.Type, null, existing.Labels, new Dictionary<string, string>(), new Dictionary<string, string>(updatedData))));
        }
    }
}
