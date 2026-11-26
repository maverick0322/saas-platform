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
            _roles[(@namespace, snapshot.Name)] = snapshot;
            return Task.FromResult(new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.Created, snapshot));
        }

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> ReplaceDatabaseRoleAsync(string @namespace, string name, IReadOnlyDictionary<string, object?> manifest, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(manifest);
            CloudNativePgDatabaseRoleSnapshot.TryParse(json, out var parsed);
            var snapshot = parsed! with { Uid = $"{parsed.Name}-uid", ResourceVersion = "2", Generation = 2, Applied = true, ObservedGeneration = 2 };
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
