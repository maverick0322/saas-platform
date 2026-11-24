using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saas.Platform.Api;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Saas.Platform.Api.Persistence;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class ProvisioningWorkerIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcquirePendingOperation_TransitionsToInProgressAndCountsInitiatedAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("acquire", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-acq-01", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;

        await using var dbContext = fixture.CreateDbContext(dbConnectionString);
        var leaseStore = new ProvisioningLeaseStore(dbContext);

        var acquired = await leaseStore.TryAcquireNextAsync(
            "worker-alpha",
            clock.GetUtcNow(),
            TimeSpan.FromSeconds(60),
            maxTotalAttempts: 4,
            ct);

        Assert.NotNull(acquired);
        Assert.Equal(opId, acquired.Token.OperationId);
        Assert.Equal("worker-alpha", acquired.Token.LeaseOwner);
        Assert.Equal(1, acquired.Token.LeaseVersion);
        Assert.Equal(1, acquired.AttemptCount);
        Assert.Equal(created.InitialProvisioning.ProvisioningRequestId, acquired.ProvisioningRequestId);
        Assert.Equal(clock.GetUtcNow().AddSeconds(60), acquired.LeaseUntilUtc);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var persisted = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        Assert.Equal(ProvisioningOperationStatus.InProgress, persisted.Status);
        Assert.Equal("worker-alpha", persisted.LeaseOwner);
        Assert.Equal(1, persisted.LeaseVersion);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.NotNull(persisted.LastAttemptAtUtc);
    }

    [Fact]
    public async Task ConcurrentWorkers_AcquireSamePendingOperationOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("conc_acquire", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-conc-01", clock, ct);

        using var barrier = new SemaphoreSlim(0, 2);

        async Task<AcquiredProvisioningOperation?> AttemptAcquireAsync(string workerId)
        {
            await using var context = fixture.CreateDbContext(dbConnectionString);
            var store = new ProvisioningLeaseStore(context);
            await barrier.WaitAsync(ct);
            return await store.TryAcquireNextAsync(
                workerId,
                clock.GetUtcNow(),
                TimeSpan.FromSeconds(60),
                maxTotalAttempts: 4,
                ct);
        }

        var t1 = AttemptAcquireAsync("worker-1");
        var t2 = AttemptAcquireAsync("worker-2");
        barrier.Release(2);
        var results = await Task.WhenAll(t1, t2);

        var winner = Assert.Single(results, r => r is not null)!;
        Assert.Single(results, r => r is null);
        Assert.Equal(1, winner.Token.LeaseVersion);
        Assert.Equal(1, winner.AttemptCount);
    }

    [Fact]
    public async Task LeaseExpiration_AllowsRecoveryAndRejectsStaleWorkerFencedWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("lease_fence", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-lease-01", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;

        await using var workerAContext = fixture.CreateDbContext(dbConnectionString);
        await using var workerBContext = fixture.CreateDbContext(dbConnectionString);
        var storeA = new ProvisioningLeaseStore(workerAContext);
        var storeB = new ProvisioningLeaseStore(workerBContext);

        var leaseA = await storeA.TryAcquireNextAsync("worker-a", clock.GetUtcNow(), TimeSpan.FromSeconds(30), 4, ct);
        Assert.NotNull(leaseA);
        Assert.Equal(1, leaseA.Token.LeaseVersion);

        // Before lease expiration, Worker B cannot acquire.
        clock.Advance(TimeSpan.FromSeconds(10));
        var earlyB = await storeB.TryAcquireNextAsync("worker-b", clock.GetUtcNow(), TimeSpan.FromSeconds(30), 4, ct);
        Assert.Null(earlyB);

        // After lease expiration, Worker B recovers with LeaseVersion = 2 and AttemptCount = 2.
        clock.Advance(TimeSpan.FromSeconds(25));
        var leaseB = await storeB.TryAcquireNextAsync("worker-b", clock.GetUtcNow(), TimeSpan.FromSeconds(30), 4, ct);
        Assert.NotNull(leaseB);
        Assert.Equal("worker-b", leaseB.Token.LeaseOwner);
        Assert.Equal(2, leaseB.Token.LeaseVersion);
        Assert.Equal(2, leaseB.AttemptCount);
        Assert.Equal(ProvisioningStageState.VerificationRequired, leaseB.InfrastructureStageState);

        // Worker B confirms Stage 1.
        Assert.True(await storeB.TryRecordStageConfirmedAsync(
            leaseB.Token,
            ProvisioningStage.InfrastructureInstance,
            "cnpg-clinica-lease-01",
            clock.GetUtcNow(),
            ct));

        // Stale Worker A cannot renew, record stages, or release with failures.
        Assert.False(await storeA.TryRenewLeaseAsync(leaseA.Token, clock.GetUtcNow(), TimeSpan.FromSeconds(30), ct));
        Assert.False(await storeA.TryRecordStageConfirmedAsync(
            leaseA.Token,
            ProvisioningStage.ServiceDatabases,
            null,
            clock.GetUtcNow(),
            ct));
        Assert.False(await storeA.TryReleaseWithRecoverableFailureAsync(
            leaseA.Token,
            null,
            "STALE_ERROR",
            "Should not overwrite",
            clock.GetUtcNow(),
            clock.GetUtcNow().AddSeconds(15),
            false,
            ct));
        Assert.False(await storeA.TryReleaseWithTerminalFailureAsync(
            leaseA.Token,
            "STALE_TERMINAL",
            "Should not overwrite",
            clock.GetUtcNow(),
            ct));

        // Recreate DbContext and confirm Worker B's state is intact.
        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var persisted = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        Assert.Equal(ProvisioningOperationStatus.InProgress, persisted.Status);
        Assert.Equal("worker-b", persisted.LeaseOwner);
        Assert.Equal(2, persisted.LeaseVersion);
        Assert.Equal(ProvisioningStageState.Confirmed, persisted.InfrastructureStageState);
        Assert.Equal(ProvisioningStageState.Pending, persisted.ServiceDatabasesStageState);
        Assert.Equal("cnpg-clinica-lease-01", persisted.InfrastructureResourceRef);
    }

    [Fact]
    public async Task AttemptCounting_LimitedRetries_AndExponentialBackoff_ExhaustSafely()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("backoff", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-backoff-01", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;
        var expectedReqId = created.InitialProvisioning.ProvisioningRequestId;

        var options = new ProvisioningWorkerOptions
        {
            Enabled = true,
            MaxAutomaticRetries = 2, // 3 total attempts
            LeaseDurationSeconds = 60,
            InitialRetryDelaySeconds = 15,
            MaxRetryDelaySeconds = 300
        };

        var executor = new ScriptedProvisioningStageExecutor(_ =>
            new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TransientFailure,
                ErrorCode: "CNPG_UNAVAILABLE",
                ErrorSummary: "Kubernetes API temporarily unavailable; token=TempToken999"));

        // Attempt 1 -> DeferredRetry (+15s)
        var cycle1 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-1", ct);
        Assert.Equal(ProvisioningCycleOutcome.DeferredRetry, cycle1.Outcome);
        Assert.Equal(1, cycle1.AttemptCount);

        // Repeated poll before backoff elapsed does NOT acquire or increment attempts.
        clock.Advance(TimeSpan.FromSeconds(5));
        var idleBeforeBackoff = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-1", ct);
        Assert.Equal(ProvisioningCycleOutcome.Idle, idleBeforeBackoff.Outcome);

        // Attempt 2 after 15s -> DeferredRetry (+30s)
        clock.Advance(TimeSpan.FromSeconds(11));
        var cycle2 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-1", ct);
        Assert.Equal(ProvisioningCycleOutcome.DeferredRetry, cycle2.Outcome);
        Assert.Equal(2, cycle2.AttemptCount);

        await using (var midContext = fixture.CreateDbContext(dbConnectionString))
        {
            var midOp = await midContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
            Assert.Equal(2, midOp.AttemptCount);
            Assert.Equal(clock.GetUtcNow().AddSeconds(30), midOp.NextAttemptAtUtc);
            Assert.False(midOp.ManualInterventionRequired);
        }

        // Attempt 3 after 30s -> RetriesExhausted, manual intervention required, no automatic retry.
        clock.Advance(TimeSpan.FromSeconds(31));
        var cycle3 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-1", ct);
        Assert.Equal(ProvisioningCycleOutcome.RetriesExhausted, cycle3.Outcome);
        Assert.Equal(3, cycle3.AttemptCount);

        // Further polls remain Idle.
        clock.Advance(TimeSpan.FromHours(1));
        var cycle4 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-1", ct);
        Assert.Equal(ProvisioningCycleOutcome.Idle, cycle4.Outcome);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var loaded = await new GetTenantByIdHandler(verifyContext).HandleAsync(new GetTenantByIdQuery(created.Id), ct);
        Assert.NotNull(loaded?.InitialProvisioning);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, loaded.InitialProvisioning.Status);
        Assert.Equal(3, loaded.InitialProvisioning.AttemptCount);
        Assert.Null(loaded.InitialProvisioning.NextAttemptAtUtc);
        Assert.True(loaded.InitialProvisioning.ManualInterventionRequired);
        Assert.Equal(expectedReqId, loaded.InitialProvisioning.ProvisioningRequestId);
        Assert.Equal("Dr. Admin Inicial", loaded.InitialProvisioning.InitialAdminFullName);
        Assert.Equal("admin.inicial@clinica-backoff-01.test", loaded.InitialProvisioning.InitialAdminEmail);
        Assert.DoesNotContain("TempToken999", loaded.InitialProvisioning.LastErrorSummary!);
        Assert.Contains("[REDACTED]", loaded.InitialProvisioning.LastErrorSummary!);
        Assert.Equal(TenantOperationalState.PendienteAdmin, loaded.OperationalState);
    }

    [Fact]
    public async Task ExpiredLeaseAtMaxAttempts_IsMarkedExhaustedWithoutFurtherAcquisition()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("expired_cap", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-expcap-01", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;

        var options = new ProvisioningWorkerOptions
        {
            Enabled = true,
            MaxAutomaticRetries = 0, // 1 total attempt
            LeaseDurationSeconds = 30
        };

        await using (var storeContext = fixture.CreateDbContext(dbConnectionString))
        {
            var store = new ProvisioningLeaseStore(storeContext);
            var acquired = await store.TryAcquireNextAsync("worker-crashed", clock.GetUtcNow(), options.LeaseDuration, options.MaxTotalAttempts, ct);
            Assert.NotNull(acquired);
        }

        // Simulate worker crash beyond lease expiration.
        clock.Advance(TimeSpan.FromSeconds(45));
        var executor = new ScriptedProvisioningStageExecutor(_ =>
            new ProvisioningStageResult(ProvisioningStageOutcomeKind.StageVerified, TechnicalReference: "unused"));

        var cycle = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-recovery", ct);
        Assert.Equal(ProvisioningCycleOutcome.Idle, cycle.Outcome);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var op = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, op.Status);
        Assert.Equal(1, op.AttemptCount);
        Assert.True(op.ManualInterventionRequired);
        Assert.Null(op.LeaseOwner);
        Assert.Null(op.LeaseUntilUtc);
        Assert.Equal(ProvisioningStageState.VerificationRequired, op.InfrastructureStageState);
        Assert.Equal("RETRIES_EXHAUSTED_LEASE_EXPIRED", op.LastErrorCode);
    }

    [Fact]
    public async Task TerminalError_TransitionsToFailedTerminalAndIsNeverRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("terminal", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-term-01", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;

        var options = new ProvisioningWorkerOptions { Enabled = true, MaxAutomaticRetries = 3 };
        var executor = new ScriptedProvisioningStageExecutor(_ =>
            new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: "PROVISIONING_CONFLICT",
                ErrorSummary: "Identity payload conflict with Host=identity-db;Password=Leak999;eyJabc.def.ghi"));

        var cycle1 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-term", ct);
        Assert.Equal(ProvisioningCycleOutcome.TerminalFailed, cycle1.Outcome);

        clock.Advance(TimeSpan.FromHours(2));
        var cycle2 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-term", ct);
        Assert.Equal(ProvisioningCycleOutcome.Idle, cycle2.Outcome);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var op = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        Assert.Equal(ProvisioningOperationStatus.FailedTerminal, op.Status);
        Assert.Equal(1, op.AttemptCount);
        Assert.True(op.ManualInterventionRequired);
        Assert.Null(op.NextAttemptAtUtc);
        Assert.Equal("PROVISIONING_CONFLICT", op.LastErrorCode);
        Assert.DoesNotContain("Leak999", op.LastErrorSummary!);
        Assert.DoesNotContain("eyJabc", op.LastErrorSummary!);
    }

    [Fact]
    public async Task ConfirmedStagesAreNotDuplicatedAndTimeoutMarksVerificationRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("stages_nodup", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-nodup-01", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;

        var options = new ProvisioningWorkerOptions
        {
            Enabled = true,
            MaxAutomaticRetries = 3,
            InitialRetryDelaySeconds = 10
        };

        var callsByStage = new Dictionary<ProvisioningStage, int>();
        var observedVerificationFlags = new List<(ProvisioningStage Stage, bool RequiresVerification)>();
        var attemptPhase = 1;

        var executor = new ScriptedProvisioningStageExecutor(context =>
        {
            callsByStage[context.Stage] = callsByStage.GetValueOrDefault(context.Stage) + 1;
            observedVerificationFlags.Add((context.Stage, context.RequiresVerification));

            if (attemptPhase == 1)
            {
                return context.Stage switch
                {
                    ProvisioningStage.InfrastructureInstance => new ProvisioningStageResult(
                        ProvisioningStageOutcomeKind.StageVerified,
                        TechnicalReference: "cnpg-clinica-nodup-01"),
                    ProvisioningStage.ServiceDatabases => new ProvisioningStageResult(
                        ProvisioningStageOutcomeKind.UncertainTimeout,
                        ErrorCode: "DB_TIMEOUT",
                        ErrorSummary: "Timeout waiting for role creation"),
                    _ => throw new InvalidOperationException($"Unexpected stage {context.Stage}")
                };
            }

            // Phase 2: Infrastructure must NOT run again; ServiceDatabases runs with RequiresVerification=true.
            return context.Stage switch
            {
                ProvisioningStage.ServiceDatabases => new ProvisioningStageResult(ProvisioningStageOutcomeKind.StageVerified),
                ProvisioningStage.ClinicalPreparation => new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: "CLINICAL_RETRY",
                    ErrorSummary: "Clinical DB migrating"),
                _ => throw new InvalidOperationException($"Unexpected stage {context.Stage}")
            };
        });

        var cycle1 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-stage", ct);
        Assert.Equal(ProvisioningCycleOutcome.DeferredRetry, cycle1.Outcome);

        await using (var midContext = fixture.CreateDbContext(dbConnectionString))
        {
            var midOp = await midContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
            Assert.Equal(ProvisioningStageState.Confirmed, midOp.InfrastructureStageState);
            Assert.Equal(ProvisioningStageState.VerificationRequired, midOp.ServiceDatabasesStageState);
            Assert.Equal(ProvisioningStageState.Pending, midOp.ClinicalStageState);
        }

        attemptPhase = 2;
        clock.Advance(TimeSpan.FromSeconds(15));
        var cycle2 = await RunSingleCycleAsync(dbConnectionString, executor, clock, options, "worker-stage", ct);
        Assert.Equal(ProvisioningCycleOutcome.DeferredRetry, cycle2.Outcome);

        Assert.Equal(1, callsByStage[ProvisioningStage.InfrastructureInstance]);
        Assert.Equal(2, callsByStage[ProvisioningStage.ServiceDatabases]);
        Assert.Equal(1, callsByStage[ProvisioningStage.ClinicalPreparation]);
        Assert.Contains((ProvisioningStage.ServiceDatabases, false), observedVerificationFlags);
        Assert.Contains((ProvisioningStage.ServiceDatabases, true), observedVerificationFlags);
    }

    [Fact]
    public async Task SimulatedStagesNeverActivateClinicOrMarkCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("no_fake_act", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var createdA = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-sim-only", clock, ct);
        var options = new ProvisioningWorkerOptions { Enabled = true, MaxAutomaticRetries = 3 };

        // Case 1: Executor reports SimulatedOnly -> explicitly rejected.
        var simulatedOnlyExecutor = new ScriptedProvisioningStageExecutor(_ =>
            new ProvisioningStageResult(ProvisioningStageOutcomeKind.SimulatedOnly));

        var simCycle = await RunSingleCycleAsync(dbConnectionString, simulatedOnlyExecutor, clock, options, "worker-sim", ct);
        Assert.Equal(ProvisioningCycleOutcome.SimulatedRejected, simCycle.Outcome);

        await using (var checkSimContext = fixture.CreateDbContext(dbConnectionString))
        {
            var tenantA = await checkSimContext.Tenants.SingleAsync(t => t.Id == createdA.Id, ct);
            var opA = await checkSimContext.TenantProvisioningOperations.SingleAsync(o => o.TenantId == createdA.Id, ct);
            Assert.Equal(TenantOperationalState.PendienteAdmin, tenantA.OperationalState);
            Assert.False(tenantA.MayAuthenticate);
            Assert.Equal(ProvisioningStageState.VerificationRequired, opA.InfrastructureStageState);
            Assert.Equal("SIMULATED_EVIDENCE_REJECTED", opA.LastErrorCode);
            Assert.True(opA.ManualInterventionRequired);
        }

        // Case 2: Even when a test double verifies all 4 stages, PLAT-02C parks safely without COMPLETED or ACTIVA.
        var createdB = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-all-stages", clock, ct);
        var allVerifiedDouble = new ScriptedProvisioningStageExecutor(ctx => ctx.Stage switch
        {
            ProvisioningStage.InfrastructureInstance => new ProvisioningStageResult(ProvisioningStageOutcomeKind.StageVerified, TechnicalReference: "cnpg-ref-01"),
            ProvisioningStage.ServiceDatabases => new ProvisioningStageResult(ProvisioningStageOutcomeKind.StageVerified),
            ProvisioningStage.ClinicalPreparation => new ProvisioningStageResult(ProvisioningStageOutcomeKind.StageVerified, TechnicalReference: "20261124_init"),
            ProvisioningStage.IdentityPreparation => new ProvisioningStageResult(ProvisioningStageOutcomeKind.StageVerified, TechnicalReference: "acct-synth-01"),
            _ => throw new ArgumentOutOfRangeException()
        });

        var allCycle = await RunSingleCycleAsync(dbConnectionString, allVerifiedDouble, clock, options, "worker-sim", ct);
        Assert.Equal(ProvisioningCycleOutcome.AwaitingRealActivation, allCycle.Outcome);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var tenantB = await verifyContext.Tenants.SingleAsync(t => t.Id == createdB.Id, ct);
        var opB = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.TenantId == createdB.Id, ct);

        Assert.Equal(TenantOperationalState.PendienteAdmin, tenantB.OperationalState);
        Assert.False(tenantB.MayAuthenticate);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, opB.Status);
        Assert.Null(opB.CompletedAtUtc);
        Assert.Equal("REAL_ACTIVATION_NOT_ENABLED", opB.LastErrorCode);
        Assert.True(opB.ManualInterventionRequired);
    }

    [Fact]
    public async Task ProductionWorkerHost_DoesNotAcquireOrModifyPendingOperationsEvenWhenEnabledInConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("prod_safe", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-prod-safe", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;

        using var workerHost = PlatformHostFactory.CreateWorkerHost(
        [
            $"--ConnectionStrings:PlatformDatabase={dbConnectionString}",
            "--ProvisioningWorker:Enabled=true",
            "--ProvisioningWorker:PollIntervalSeconds=1"
        ]);

        await workerHost.StartAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        await workerHost.StopAsync(ct);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var op = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        var tenant = await verifyContext.Tenants.SingleAsync(t => t.Id == created.Id, ct);

        Assert.Equal(ProvisioningOperationStatus.Pending, op.Status);
        Assert.Equal(0, op.AttemptCount);
        Assert.Null(op.LeaseOwner);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
    }

    [Fact]
    public async Task UpgradeMigration_FromPlat02ASchema_PreservesOperationsAndAllowsWorkerAcquisition()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await fixture.CreateEmptyDatabaseAsync(
            $"plat02c_upgrade_{Guid.NewGuid():N}",
            ct);

        var existingTenantId = Guid.CreateVersion7(BaseUtc);
        var existingOpId = Guid.CreateVersion7(BaseUtc);
        var existingReqId = existingOpId.ToString("D");

        await using (var plat02aContext = fixture.CreateDbContext(dbConnectionString))
        {
            var migrator = plat02aContext.GetService<IMigrator>();
            await migrator.MigrateAsync("20261124053749_AddTenantOperationalStateAndProvisioningOperations", ct);

            await using var conn = new NpgsqlConnection(dbConnectionString);
            await conn.OpenAsync(ct);

            await using var insertTenant = new NpgsqlCommand(
                """
                INSERT INTO platform.tenants
                    (id, public_id, name, contact_email, operational_state, created_at_utc, updated_at_utc, concurrency_stamp)
                VALUES
                    (@id, 'clinica-plat02a-legado', 'Clínica PLAT-02A Previa', 'contacto@plat02a.test', 'PENDIENTE_ADMIN', @now, @now, @stamp);
                """,
                conn);
            insertTenant.Parameters.AddWithValue("id", existingTenantId);
            insertTenant.Parameters.AddWithValue("now", BaseUtc);
            insertTenant.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await insertTenant.ExecuteNonQueryAsync(ct);

            await using var insertOp = new NpgsqlCommand(
                """
                INSERT INTO platform.tenant_provisioning_operations
                    (id, tenant_id, provisioning_request_id, initial_admin_full_name, initial_admin_email, status, attempt_count, created_at_utc, updated_at_utc, concurrency_stamp)
                VALUES
                    (@opId, @tenantId, @reqId, 'Admin Previo', 'admin@plat02a.test', 'PENDING', 0, @now, @now, @stamp);
                """,
                conn);
            insertOp.Parameters.AddWithValue("opId", existingOpId);
            insertOp.Parameters.AddWithValue("tenantId", existingTenantId);
            insertOp.Parameters.AddWithValue("reqId", existingReqId);
            insertOp.Parameters.AddWithValue("now", BaseUtc);
            insertOp.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await insertOp.ExecuteNonQueryAsync(ct);
        }

        await using var upgradedContext = fixture.CreateDbContext(dbConnectionString);
        await upgradedContext.Database.MigrateAsync(ct);

        Assert.Empty(await upgradedContext.Database.GetPendingMigrationsAsync(ct));
        Assert.False(upgradedContext.Database.HasPendingModelChanges());

        var op = await upgradedContext.TenantProvisioningOperations.SingleAsync(o => o.Id == existingOpId, ct);
        Assert.Equal(0, op.LeaseVersion);
        Assert.False(op.ManualInterventionRequired);
        Assert.Equal(ProvisioningStageState.Pending, op.InfrastructureStageState);
        Assert.Equal(ProvisioningStageState.Pending, op.ServiceDatabasesStageState);
        Assert.Equal(ProvisioningStageState.Pending, op.ClinicalStageState);
        Assert.Equal(ProvisioningStageState.Pending, op.IdentityStageState);

        var leaseStore = new ProvisioningLeaseStore(upgradedContext);
        var acquired = await leaseStore.TryAcquireNextAsync("worker-upgrade", BaseUtc, TimeSpan.FromSeconds(60), 4, ct);
        Assert.NotNull(acquired);
        Assert.Equal(existingOpId, acquired.Token.OperationId);
        Assert.Equal(existingTenantId, acquired.TenantId);
        Assert.Equal(existingReqId, acquired.ProvisioningRequestId);
    }

    [Fact]
    public async Task LongRunningStageExceedingOriginalLease_RenewsLeaseAndPreventsSecondWorkerTakeover()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("long_renew", ct);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-long-stage", TimeProvider.System, ct);
        var opId = created.InitialProvisioning!.OperationId;

        var options = new ProvisioningWorkerOptions
        {
            Enabled = true,
            MaxAutomaticRetries = 3,
            LeaseDurationSeconds = 1
        };

        var stageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStageCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executor = new ScriptedProvisioningStageExecutor(async (ctx, stageToken) =>
        {
            if (ctx.Stage == ProvisioningStage.InfrastructureInstance)
            {
                stageStarted.TrySetResult();
                await allowStageCompletion.Task.WaitAsync(stageToken);
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.StageVerified,
                    TechnicalReference: "cnpg-clinica-long-stage");
            }

            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TransientFailure,
                ErrorCode: "DEFER_NEXT_STAGE",
                ErrorSummary: "Defer remaining stages after confirming long-running stage.");
        });

        var worker1Task = RunSingleCycleAsync(dbConnectionString, executor, TimeProvider.System, options, "worker-long-1", ct);
        await stageStarted.Task.WaitAsync(ct);

        // Wait strictly past the 1s original lease while worker-long-1 is still executing Stage 1.
        await Task.Delay(TimeSpan.FromMilliseconds(1350), ct);

        await using (var worker2Context = fixture.CreateDbContext(dbConnectionString))
        {
            var store2 = new ProvisioningLeaseStore(worker2Context);
            var takeoverAttempt = await store2.TryAcquireNextAsync(
                "worker-long-2",
                TimeProvider.System.GetUtcNow(),
                options.LeaseDuration,
                options.MaxTotalAttempts,
                ct);

            Assert.Null(takeoverAttempt);
        }

        allowStageCompletion.TrySetResult();
        var cycleResult = await worker1Task;

        Assert.Equal(ProvisioningCycleOutcome.DeferredRetry, cycleResult.Outcome);
        Assert.Equal(1, cycleResult.LeaseVersion);
        Assert.Equal(1, cycleResult.AttemptCount);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var op = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        var tenant = await verifyContext.Tenants.SingleAsync(t => t.Id == created.Id, ct);
        Assert.Equal(ProvisioningStageState.Confirmed, op.InfrastructureStageState);
        Assert.Equal("cnpg-clinica-long-stage", op.InfrastructureResourceRef);
        Assert.Equal(1, op.LeaseVersion);
        Assert.Equal(1, op.AttemptCount);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
    }

    [Fact]
    public async Task LeaseLostDuringStageExecution_CancelsStageAndDoesNotPersistStaleResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("lease_lost_cancel", ct);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-lose-lease", TimeProvider.System, ct);
        var opId = created.InitialProvisioning!.OperationId;

        var options = new ProvisioningWorkerOptions
        {
            Enabled = true,
            MaxAutomaticRetries = 3,
            LeaseDurationSeconds = 1
        };

        var stageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageCanceledObserved = false;

        var executor = new ScriptedProvisioningStageExecutor(async (ctx, stageToken) =>
        {
            stageStarted.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stageToken);
            }
            catch (OperationCanceledException) when (stageToken.IsCancellationRequested)
            {
                stageCanceledObserved = true;
                throw;
            }

            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.StageVerified,
                TechnicalReference: "stale-should-never-persist");
        });

        var worker1Task = RunSingleCycleAsync(dbConnectionString, executor, TimeProvider.System, options, "worker-stale", ct);
        await stageStarted.Task.WaitAsync(ct);

        // Simulate another worker taking over the lease in PostgreSQL before worker-stale renews.
        await using (var conn = new NpgsqlConnection(dbConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                UPDATE platform.tenant_provisioning_operations
                SET lease_owner = 'worker-new-owner',
                    lease_version = lease_version + 1,
                    lease_until_utc = NOW() + INTERVAL '60 seconds',
                    attempt_count = attempt_count + 1
                WHERE id = @opId;
                """,
                conn);
            cmd.Parameters.AddWithValue("opId", opId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var result = await worker1Task;

        Assert.True(stageCanceledObserved);
        Assert.Equal(ProvisioningCycleOutcome.LeaseLost, result.Outcome);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var op = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        Assert.Equal("worker-new-owner", op.LeaseOwner);
        Assert.Equal(2, op.LeaseVersion);
        Assert.Equal(ProvisioningStageState.Pending, op.InfrastructureStageState);
        Assert.Null(op.InfrastructureResourceRef);
    }

    [Fact]
    public async Task CrashAfterExternalSideEffectBeforeConfirm_RecoveryVerifiesWithoutDuplicatingEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("crash_verify", ct);
        var clock = new MutableTimeProvider(BaseUtc);

        var created = await RegisterSyntheticTenantAsync(dbConnectionString, "clinica-crash-01", clock, ct);
        var opId = created.InitialProvisioning!.OperationId;
        var expectedRequestId = created.InitialProvisioning.ProvisioningRequestId;

        var createdClusters = new List<string>();
        var verificationFlagsObserved = new List<bool>();

        var crashingExecutor = new ScriptedProvisioningStageExecutor((ctx, _) =>
        {
            if (ctx.Stage == ProvisioningStage.InfrastructureInstance)
            {
                createdClusters.Add($"cnpg-{ctx.TenantPublicId}");
                throw new InvalidOperationException("Simulated worker crash right after creating CNPG cluster and before persisting CONFIRMED.");
            }

            return Task.FromResult(new ProvisioningStageResult(ProvisioningStageOutcomeKind.AdapterNotConfigured));
        });

        var options = new ProvisioningWorkerOptions
        {
            Enabled = true,
            MaxAutomaticRetries = 3,
            LeaseDurationSeconds = 30
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunSingleCycleAsync(dbConnectionString, crashingExecutor, clock, options, "worker-crashed", ct));

        Assert.Single(createdClusters);

        await using (var crashedContext = fixture.CreateDbContext(dbConnectionString))
        {
            var crashedOp = await crashedContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
            Assert.Equal(ProvisioningOperationStatus.InProgress, crashedOp.Status);
            Assert.Equal(ProvisioningStageState.Pending, crashedOp.InfrastructureStageState);
            Assert.Equal("worker-crashed", crashedOp.LeaseOwner);
            Assert.Equal(1, crashedOp.AttemptCount);
        }

        // Advance past lease expiration so Worker 2 recovers the abandoned IN_PROGRESS operation.
        clock.Advance(TimeSpan.FromSeconds(45));

        var recoveryExecutor = new ScriptedProvisioningStageExecutor((ctx, _) =>
        {
            if (ctx.Stage == ProvisioningStage.InfrastructureInstance)
            {
                verificationFlagsObserved.Add(ctx.RequiresVerification);

                var expectedClusterRef = $"cnpg-{ctx.TenantPublicId}";
                if (ctx.RequiresVerification && createdClusters.Contains(expectedClusterRef))
                {
                    // Existing resource verified without creating a duplicate.
                    return Task.FromResult(new ProvisioningStageResult(
                        ProvisioningStageOutcomeKind.StageVerified,
                        TechnicalReference: expectedClusterRef));
                }

                createdClusters.Add(expectedClusterRef);
                return Task.FromResult(new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.StageVerified,
                    TechnicalReference: expectedClusterRef));
            }

            return Task.FromResult(new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.StageVerified,
                TechnicalReference: ctx.Stage switch
                {
                    ProvisioningStage.ClinicalPreparation => "20261124_init",
                    ProvisioningStage.IdentityPreparation => "acct-crash-01",
                    _ => null
                }));
        });

        var recoveryCycle = await RunSingleCycleAsync(dbConnectionString, recoveryExecutor, clock, options, "worker-recovery", ct);

        Assert.Equal(ProvisioningCycleOutcome.AwaitingRealActivation, recoveryCycle.Outcome);
        Assert.Equal([true], verificationFlagsObserved);
        Assert.Single(createdClusters);

        await using var verifyContext = fixture.CreateDbContext(dbConnectionString);
        var op = await verifyContext.TenantProvisioningOperations.SingleAsync(o => o.Id == opId, ct);
        var tenant = await verifyContext.Tenants.SingleAsync(t => t.Id == created.Id, ct);

        Assert.Equal(ProvisioningStageState.Confirmed, op.InfrastructureStageState);
        Assert.Equal("cnpg-clinica-crash-01", op.InfrastructureResourceRef);
        Assert.Equal(2, op.LeaseVersion);
        Assert.Equal(2, op.AttemptCount);
        Assert.Equal(expectedRequestId, op.ProvisioningRequestId);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, op.Status);
        Assert.Equal("REAL_ACTIVATION_NOT_ENABLED", op.LastErrorCode);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);
    }

    private async Task<string> CreateMigratedIsolatedDatabaseAsync(string suffix, CancellationToken ct)
    {
        var dbConnectionString = await fixture.CreateEmptyDatabaseAsync(
            $"plat02c_{suffix}_{Guid.NewGuid():N}",
            ct);

        await using var context = fixture.CreateDbContext(dbConnectionString);
        await context.Database.MigrateAsync(ct);
        return dbConnectionString;
    }

    private async Task<TenantDetails> RegisterSyntheticTenantAsync(
        string dbConnectionString,
        string publicId,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        await using var context = fixture.CreateDbContext(dbConnectionString);
        var handler = new RegisterTenantHandler(
            context,
            timeProvider,
            NullLogger<RegisterTenantHandler>.Instance);

        var result = await handler.HandleAsync(
            new RegisterTenantCommand(
                publicId,
                $"Clínica {publicId}",
                $"contacto@{publicId}.test",
                "Dr. Admin Inicial",
                $"admin.inicial@{publicId}.test",
                MaxActiveAccounts: 25),
            ct);

        Assert.Equal(RegisterTenantStatus.Created, result.Status);
        return result.Tenant!;
    }

    private async Task<ProvisioningCycleResult> RunSingleCycleAsync(
        string dbConnectionString,
        IProvisioningStageExecutor stageExecutor,
        TimeProvider timeProvider,
        ProvisioningWorkerOptions options,
        string workerId,
        CancellationToken ct)
    {
        await using var context = fixture.CreateDbContext(dbConnectionString);
        var leaseStore = new ProvisioningLeaseStore(context);
        var processor = new ProvisioningOperationProcessor(
            leaseStore,
            stageExecutor,
            timeProvider,
            options,
            NullLogger<ProvisioningOperationProcessor>.Instance);

        return await processor.ProcessNextAsync(workerId, ct);
    }

    private sealed class ScriptedProvisioningStageExecutor : IProvisioningStageExecutor
    {
        private readonly Func<ProvisioningStageContext, CancellationToken, Task<ProvisioningStageResult>> _script;

        public ScriptedProvisioningStageExecutor(Func<ProvisioningStageContext, ProvisioningStageResult> script)
            : this((context, _) => Task.FromResult(script(context)))
        {
        }

        public ScriptedProvisioningStageExecutor(Func<ProvisioningStageContext, CancellationToken, Task<ProvisioningStageResult>> script)
        {
            _script = script;
        }

        public bool SupportsRealProvisioning => false;

        public Task<ProvisioningStageResult> ExecuteStageAsync(
            ProvisioningStageContext context,
            CancellationToken cancellationToken) =>
            _script(context, cancellationToken);
    }
}
