using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.AdminHttp;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class CloudNativePgWorkerIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task ProcessNextAsync_ProvisionsClusterConfirmsInfrastructureAndStopsSafelyAtServiceDatabases()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("cnpg_happy", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        var (tenantId, operationId) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-cnpg-happy",
            clock,
            ct);

        var cnpgOptions = CreateFastOptions();
        var client = new ScriptedClusterClient(
            (_, _) =>
                [
                    CloudNativePgGetResult.Missing(),
                    CloudNativePgGetResult.FoundCluster(BuildSnapshot(tenantId, operationId, cnpgOptions, readyInstances: 0, phase: "Setting up primary", readyStatus: "False")),
                    CloudNativePgGetResult.FoundCluster(BuildSnapshot(tenantId, operationId, cnpgOptions))
                ],
            (_, _) => CloudNativePgCreateResult.CreatedCluster(
                BuildSnapshot(tenantId, operationId, cnpgOptions, readyInstances: 0, phase: "Setting up primary", readyStatus: "False")));

        var processor = CreateProcessor(connectionString, client, cnpgOptions, clock);
        var result = await processor.ProcessNextAsync("worker-cnpg-happy", ct);

        Assert.Equal(ProvisioningCycleOutcome.AdapterNotConfigured, result.Outcome);
        Assert.Equal(ProvisioningStage.ServiceDatabases, result.LastStage);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(1, client.CreateCalls);

        await using var db = fixture.CreateDbContext(connectionString);
        var operation = await db.TenantProvisioningOperations
            .AsNoTracking()
            .SingleAsync(item => item.Id == operationId, ct);
        var tenant = await db.Tenants
            .AsNoTracking()
            .SingleAsync(item => item.Id == tenantId, ct);

        var expectedRef = $"{cnpgOptions.Namespace}/pg-{tenantId:N}@99999999-8888-7777-6666-555555555555";
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, operation.Status);
        Assert.Equal(ProvisioningStage.ServiceDatabases, operation.GetNextPendingStage());
        Assert.Equal(ProvisioningStageState.Confirmed, operation.InfrastructureStageState);
        Assert.Equal(expectedRef, operation.InfrastructureResourceRef);
        Assert.Equal(ProvisioningStageState.Pending, operation.ServiceDatabasesStageState);
        Assert.True(operation.ManualInterventionRequired);
        Assert.Null(operation.NextAttemptAtUtc);
        Assert.Equal("ADAPTER_NOT_CONFIGURED", operation.LastErrorCode);

        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);

        var getHandler = new GetTenantByIdHandler(db);
        var adminView = await getHandler.HandleAsync(new GetTenantByIdQuery(tenantId), ct);
        Assert.NotNull(adminView);
        Assert.Equal("PENDIENTE_ADMIN", adminView.OperationalStateCode);
        Assert.False(adminView.MayAuthenticate);
        Assert.NotNull(adminView.InitialProvisioning);
        Assert.Equal(ProvisioningStageState.Confirmed, adminView.InitialProvisioning.InfrastructureStage);
        Assert.Equal(ProvisioningStageState.Pending, adminView.InitialProvisioning.ServiceDatabasesStage);
        Assert.False(string.IsNullOrWhiteSpace(ProvisioningAdministrativeErrorCatalog.ResolveControlledSummary(
            operation.LastErrorCode,
            operation.Status,
            operation.ManualInterventionRequired)));
    }

    [Fact]
    public async Task ProcessNextAsync_RecoversFromUncertainTimeoutAndAdoptsExistingClusterWithoutDuplicateCreate()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("cnpg_timeout", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.Zero));
        var (tenantId, operationId) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-cnpg-timeout",
            clock,
            ct);

        var cnpgOptions = CreateFastOptions();
        var createAttempts = 0;

        var firstAttemptClient = new DelegateClusterClient(
            (_, _) => Task.FromResult(CloudNativePgGetResult.Missing()),
            (_, _) =>
            {
                createAttempts++;
                return Task.FromResult(CloudNativePgCreateResult.Uncertain(
                    "CNPG_CREATE_TIMEOUT_UNCERTAIN",
                    "Timed out submitting CloudNativePG cluster creation."));
            });

        var workerOptions = CreateWorkerOptions(retryDelaySeconds: 5);
        var firstProcessor = CreateProcessor(connectionString, firstAttemptClient, cnpgOptions, clock, workerOptions);
        var firstResult = await firstProcessor.ProcessNextAsync("worker-cnpg-1", ct);

        Assert.Equal(ProvisioningCycleOutcome.DeferredRetry, firstResult.Outcome);
        Assert.Equal(ProvisioningStage.InfrastructureInstance, firstResult.LastStage);
        Assert.Equal(1, createAttempts);

        await using (var verifyDb = fixture.CreateDbContext(connectionString))
        {
            var afterFirst = await verifyDb.TenantProvisioningOperations
                .AsNoTracking()
                .SingleAsync(item => item.Id == operationId, ct);
            Assert.Equal(ProvisioningOperationStatus.FailedRetryable, afterFirst.Status);
            Assert.Equal(ProvisioningStageState.VerificationRequired, afterFirst.InfrastructureStageState);
            Assert.Equal("CNPG_CREATE_TIMEOUT_UNCERTAIN", afterFirst.LastErrorCode);
            Assert.Null(afterFirst.InfrastructureResourceRef);
            Assert.NotNull(afterFirst.NextAttemptAtUtc);
        }

        // El Cluster si llego a existir en Kubernetes tras el timeout: la recuperacion debe detectarlo con GET
        clock.Advance(TimeSpan.FromSeconds(10));
        var recoveryClient = new DelegateClusterClient(
            (_, _) => Task.FromResult(CloudNativePgGetResult.FoundCluster(BuildSnapshot(tenantId, operationId, cnpgOptions))),
            (_, _) =>
            {
                createAttempts++;
                throw new InvalidOperationException("Duplicate CreateClusterAsync call must never occur during recovery.");
            });

        var secondProcessor = CreateProcessor(connectionString, recoveryClient, cnpgOptions, clock, workerOptions);
        var secondResult = await secondProcessor.ProcessNextAsync("worker-cnpg-2", ct);

        Assert.Equal(ProvisioningCycleOutcome.AdapterNotConfigured, secondResult.Outcome);
        Assert.Equal(ProvisioningStage.ServiceDatabases, secondResult.LastStage);
        Assert.Equal(1, createAttempts);

        await using var finalDb = fixture.CreateDbContext(connectionString);
        var finalOp = await finalDb.TenantProvisioningOperations
            .AsNoTracking()
            .SingleAsync(item => item.Id == operationId, ct);
        var tenant = await finalDb.Tenants
            .AsNoTracking()
            .SingleAsync(item => item.Id == tenantId, ct);

        Assert.Equal(ProvisioningStageState.Confirmed, finalOp.InfrastructureStageState);
        Assert.NotNull(finalOp.InfrastructureResourceRef);
        Assert.Equal(2, finalOp.AttemptCount);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);
    }

    [Fact]
    public async Task ProcessNextAsync_FencesOutStaleWorkerWhenLeaseIsLostDuringSlowClusterWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("cnpg_fence", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var (tenantId, operationId) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-cnpg-fence",
            clock,
            ct);

        var cnpgOptions = CreateFastOptions();
        var workerOptions = CreateWorkerOptions(leaseDurationSeconds: 6, retryDelaySeconds: 5);

        var enteredGet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slowClient = new DelegateClusterClient(
            async (_, _, token) =>
            {
                enteredGet.TrySetResult();
                await releaseGet.Task.WaitAsync(token);
                return CloudNativePgGetResult.FoundCluster(BuildSnapshot(tenantId, operationId, cnpgOptions));
            },
            (_, _, _) => throw new InvalidOperationException("Create not expected in slow-get fencing test."));

        var workerA = CreateProcessor(connectionString, slowClient, cnpgOptions, clock, workerOptions);
        var workerATask = workerA.ProcessNextAsync("worker-stale-a", ct);

        await enteredGet.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Expiramos el lease y dejamos que otro trabajador tome control de la operacion
        clock.Advance(TimeSpan.FromSeconds(10));

        await using var stealDb = fixture.CreateDbContext(connectionString);
        var stealStore = new ProvisioningLeaseStore(stealDb);
        var stolenLease = await stealStore.TryAcquireNextAsync(
            "worker-owner-b",
            clock.GetUtcNow(),
            TimeSpan.FromSeconds(30),
            workerOptions.MaxTotalAttempts,
            ct);

        Assert.NotNull(stolenLease);
        Assert.True(stolenLease.Token.LeaseVersion > 1);

        // Esperamos a que el renovador periodico de A detecte perdida de lease y cancele stageCts.Token
        var workerAResult = await workerATask.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(ProvisioningCycleOutcome.LeaseLost, workerAResult.Outcome);

        releaseGet.TrySetResult();

        await using var checkDb = fixture.CreateDbContext(connectionString);
        var persisted = await checkDb.TenantProvisioningOperations
            .AsNoTracking()
            .SingleAsync(item => item.Id == operationId, ct);

        Assert.Equal("worker-owner-b", persisted.LeaseOwner);
        Assert.Equal(stolenLease.Token.LeaseVersion, persisted.LeaseVersion);
        Assert.Null(persisted.InfrastructureResourceRef);
        Assert.NotEqual(ProvisioningStageState.Confirmed, persisted.InfrastructureStageState);
    }

    [Fact]
    public async Task ProcessNextAsync_RejectsEmptyOrInvalidTechnicalReferenceWhenRealProvisioningConfirms()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await CreateMigratedIsolatedDatabaseAsync("cnpg_badref", ct);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero));
        var (_, operationId) = await RegisterSyntheticTenantAsync(
            connectionString,
            "cln-cnpg-badref",
            clock,
            ct);

        var badRefExecutor = new InvalidRefRealStageExecutor();
        await using var db = fixture.CreateDbContext(connectionString);
        var leaseStore = new ProvisioningLeaseStore(db);
        var processor = new ProvisioningOperationProcessor(
            leaseStore,
            badRefExecutor,
            clock,
            CreateWorkerOptions(),
            NullLogger<ProvisioningOperationProcessor>.Instance);

        var result = await processor.ProcessNextAsync("worker-badref", ct);

        Assert.Equal(ProvisioningCycleOutcome.InvalidEvidence, result.Outcome);

        var persisted = await db.TenantProvisioningOperations
            .AsNoTracking()
            .SingleAsync(item => item.Id == operationId, ct);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, persisted.Status);
        Assert.Equal(ProvisioningStageState.VerificationRequired, persisted.InfrastructureStageState);
        Assert.Equal("INVALID_TECHNICAL_REFERENCE", persisted.LastErrorCode);
        Assert.Null(persisted.InfrastructureResourceRef);
        Assert.True(persisted.ManualInterventionRequired);
    }

    private async Task<string> CreateMigratedIsolatedDatabaseAsync(string prefix, CancellationToken ct)
    {
        var dbName = $"{prefix}_{Guid.NewGuid():N}"[..36];
        var connectionString = await fixture.CreateEmptyDatabaseAsync(dbName, ct);

        await using var db = fixture.CreateDbContext(connectionString);
        await db.Database.MigrateAsync(ct);
        return connectionString;
    }

    private async Task<(Guid TenantId, Guid OperationId)> RegisterSyntheticTenantAsync(
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
        Assert.NotNull(result.Tenant);
        Assert.NotNull(result.Tenant.InitialProvisioning);
        return (result.Tenant.Id, result.Tenant.InitialProvisioning.OperationId);
    }

    private ProvisioningOperationProcessor CreateProcessor(
        string connectionString,
        ICloudNativePgClusterClient client,
        CloudNativePgOptions cnpgOptions,
        TimeProvider clock,
        ProvisioningWorkerOptions? workerOptions = null)
    {
        var db = fixture.CreateDbContext(connectionString);
        var leaseStore = new ProvisioningLeaseStore(db);
        var executor = new CloudNativePgProvisioningStageExecutor(
            client,
            cnpgOptions,
            clock,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        return new ProvisioningOperationProcessor(
            leaseStore,
            executor,
            clock,
            workerOptions ?? CreateWorkerOptions(),
            NullLogger<ProvisioningOperationProcessor>.Instance);
    }

    private static CloudNativePgOptions CreateFastOptions()
        => new()
        {
            ApiCallTimeoutSeconds = 2,
            ReadinessTimeoutSeconds = 5,
            ReadinessPollIntervalSeconds = 1
        };

    private static ProvisioningWorkerOptions CreateWorkerOptions(
        int leaseDurationSeconds = 30,
        int retryDelaySeconds = 5)
        => new()
        {
            Enabled = true,
            Mode = ProvisioningExecutionMode.CloudNativePg,
            MaxAutomaticRetries = 3,
            LeaseDurationSeconds = leaseDurationSeconds,
            InitialRetryDelaySeconds = retryDelaySeconds,
            MaxRetryDelaySeconds = 60,
            PollIntervalSeconds = 1
        };

    private static CloudNativePgClusterSnapshot BuildSnapshot(
        Guid tenantId,
        Guid operationId,
        CloudNativePgOptions options,
        int readyInstances = 1,
        string phase = "Cluster in healthy state",
        string readyStatus = "True")
        => new(
            CloudNativePgClusterSpec.BuildClusterName(tenantId),
            options.Namespace,
            "99999999-8888-7777-6666-555555555555",
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
            readyInstances,
            "pg-primary-1",
            "pg-primary-1",
            phase,
            readyStatus,
            "ClusterIsReady",
            1);

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class ScriptedClusterClient(
        Func<string, string, IReadOnlyList<CloudNativePgGetResult>> getSequenceFactory,
        Func<string, IReadOnlyDictionary<string, object?>, CloudNativePgCreateResult> onCreate)
        : ICloudNativePgClusterClient
    {
        private Queue<CloudNativePgGetResult>? _gets;

        public int CreateCalls { get; private set; }

        public void EnsureInitialized()
        {
        }

        public Task<CloudNativePgGetResult> GetClusterAsync(
            string @namespace,
            string clusterName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _gets ??= new Queue<CloudNativePgGetResult>(getSequenceFactory(@namespace, clusterName));
            return Task.FromResult(_gets.Dequeue());
        }

        public Task<CloudNativePgCreateResult> CreateClusterAsync(
            string @namespace,
            IReadOnlyDictionary<string, object?> manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            return Task.FromResult(onCreate(@namespace, manifest));
        }
    }

    private sealed class DelegateClusterClient : ICloudNativePgClusterClient
    {
        private readonly Func<string, string, CancellationToken, Task<CloudNativePgGetResult>> _onGet;
        private readonly Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<CloudNativePgCreateResult>> _onCreate;

        public DelegateClusterClient(
            Func<string, string, Task<CloudNativePgGetResult>> onGet,
            Func<string, IReadOnlyDictionary<string, object?>, Task<CloudNativePgCreateResult>> onCreate)
        {
            _onGet = (ns, name, _) => onGet(ns, name);
            _onCreate = (ns, manifest, _) => onCreate(ns, manifest);
        }

        public DelegateClusterClient(
            Func<string, string, CancellationToken, Task<CloudNativePgGetResult>> onGet,
            Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<CloudNativePgCreateResult>> onCreate)
        {
            _onGet = onGet;
            _onCreate = onCreate;
        }

        public void EnsureInitialized()
        {
        }

        public Task<CloudNativePgGetResult> GetClusterAsync(
            string @namespace,
            string clusterName,
            CancellationToken cancellationToken)
            => _onGet(@namespace, clusterName, cancellationToken);

        public Task<CloudNativePgCreateResult> CreateClusterAsync(
            string @namespace,
            IReadOnlyDictionary<string, object?> manifest,
            CancellationToken cancellationToken)
            => _onCreate(@namespace, manifest, cancellationToken);
    }

    private sealed class InvalidRefRealStageExecutor : IProvisioningStageExecutor
    {
        public bool SupportsRealProvisioning => true;

        public Task<ProvisioningStageResult> ExecuteStageAsync(
            ProvisioningStageContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.StageVerified,
                TechnicalReference: "Host=db;Password=secret-not-allowed"));
    }
}
