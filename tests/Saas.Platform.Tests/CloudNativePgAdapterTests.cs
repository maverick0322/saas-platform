using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Saas.Platform.Api;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.AdminHttp;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class CloudNativePgAdapterTests
{
    private static readonly Guid SyntheticTenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SyntheticOperationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void CloudNativePgOptions_DefaultsMatchVerifiedDb01cProfileAndValidate()
    {
        var options = new CloudNativePgOptions();

        options.Validate();

        Assert.Equal("saas-tenant-databases", options.Namespace);
        Assert.Equal(1, options.Instances);
        Assert.Equal("local-path", options.StorageClass);
        Assert.Equal("2Gi", options.StorageSize);
        Assert.Equal("100m", options.CpuRequest);
        Assert.Equal("750m", options.CpuLimit);
        Assert.Equal("256Mi", options.MemoryRequest);
        Assert.Equal("768Mi", options.MemoryLimit);
        Assert.Contains("@sha256:", options.ImageName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "ghcr.io/cloudnative-pg/postgresql:17.12-system-trixie@sha256:0f34d3fb12f415713ec94c3f2aa8295c7975f7114a0a8d360f26c958253bd032", 1, "local-path", "2Gi", "100m", "750m", "256Mi", "768Mi")]
    [InlineData("namespace-way-too-long-exceeding-fifty-four-characters-123", "ghcr.io/cloudnative-pg/postgresql:17.12-system-trixie@sha256:0f34d3fb12f415713ec94c3f2aa8295c7975f7114a0a8d360f26c958253bd032", 1, "local-path", "2Gi", "100m", "750m", "256Mi", "768Mi")]
    [InlineData("saas-tenant-databases", "ghcr.io/cloudnative-pg/postgresql:latest", 1, "local-path", "2Gi", "100m", "750m", "256Mi", "768Mi")]
    [InlineData("saas-tenant-databases", "ghcr.io/cloudnative-pg/postgresql", 1, "local-path", "2Gi", "100m", "750m", "256Mi", "768Mi")]
    [InlineData("saas-tenant-databases", "ghcr.io/cloudnative-pg/postgresql:17.12", 2, "local-path", "2Gi", "100m", "750m", "256Mi", "768Mi")]
    [InlineData("saas-tenant-databases", "ghcr.io/cloudnative-pg/postgresql:17.12", 1, "local-path", "2Gi", "800m", "750m", "256Mi", "768Mi")]
    [InlineData("saas-tenant-databases", "ghcr.io/cloudnative-pg/postgresql:17.12", 1, "local-path", "2Gi", "100m", "750m", "1Gi", "768Mi")]
    public void CloudNativePgOptions_RejectsUnsafeOrInconsistentConfiguration(
        string @namespace,
        string imageName,
        int instances,
        string storageClass,
        string storageSize,
        string cpuRequest,
        string cpuLimit,
        string memoryRequest,
        string memoryLimit)
    {
        var options = new CloudNativePgOptions
        {
            Namespace = @namespace,
            ImageName = imageName,
            Instances = instances,
            StorageClass = storageClass,
            StorageSize = storageSize,
            CpuRequest = cpuRequest,
            CpuLimit = cpuLimit,
            MemoryRequest = memoryRequest,
            MemoryLimit = memoryLimit
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void ClusterSpec_BuildsDeterministicManifestWithoutSecretsPiiOrRoles()
    {
        var options = new CloudNativePgOptions();
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);

        var clusterName = CloudNativePgClusterSpec.BuildClusterName(context.TenantId);
        var manifest = CloudNativePgClusterSpec.BuildClusterManifest(context, options);
        var json = JsonSerializer.Serialize(manifest);

        Assert.Equal("pg-11111111222233334444555555555555", clusterName);
        Assert.True(clusterName.Length <= 50);

        Assert.Contains("\"apiVersion\":\"postgresql.cnpg.io/v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"Cluster\"", json, StringComparison.Ordinal);
        Assert.Contains("\"app.kubernetes.io/managed-by\":\"saas-platform\"", json, StringComparison.Ordinal);
        Assert.Contains("\"vital.saas/tenant-id\":\"11111111-2222-3333-4444-555555555555\"", json, StringComparison.Ordinal);
        Assert.Contains("\"instances\":1", json, StringComparison.Ordinal);

        Assert.DoesNotContain("pg_hba", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DatabaseRole", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Clinica Demo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin.demo@synthetic.test", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClusterSpec_TechnicalReferenceFitsDatabaseLimitAndSurvivesSanitization()
    {
        var clusterName = CloudNativePgClusterSpec.BuildClusterName(SyntheticTenantId);
        var valid = CloudNativePgClusterSpec.TryBuildTechnicalReference(
            "saas-tenant-databases",
            clusterName,
            "99999999-8888-7777-6666-555555555555",
            out var reference);

        Assert.True(valid);
        Assert.Equal("saas-tenant-databases/pg-11111111222233334444555555555555@99999999-8888-7777-6666-555555555555", reference);
        Assert.True(reference.Length <= TenantProvisioningOperation.TechnicalReferenceMaxLength);
        Assert.Equal(reference, TenantProvisioningOperation.SanitizeTechnicalReference(reference));
    }

    [Fact]
    public void Evaluator_RejectsForeignUnownedOrIncompatibleClusters()
    {
        var options = new CloudNativePgOptions();
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);

        var foreignOwner = BuildSnapshot(options, context, managedBy: "argocd");
        var wrongTenant = BuildSnapshot(options, context, tenantLabel: Guid.NewGuid().ToString("D"));
        var wrongStorage = BuildSnapshot(options, context, storageSize: "10Gi");
        var wrongInstances = BuildSnapshot(options, context, instances: 3);
        var deleting = BuildSnapshot(options, context, deletionTimestamp: "2026-11-25T00:00:00Z");
        var unrecoverable = BuildSnapshot(options, context, phase: "Cluster is unrecoverable and needs manual intervention");

        Assert.Equal("CNPG_CLUSTER_OWNERSHIP_CONFLICT", CloudNativePgClusterEvaluator.Evaluate(foreignOwner, context, options).ErrorCode);
        Assert.Equal("CNPG_CLUSTER_OWNERSHIP_CONFLICT", CloudNativePgClusterEvaluator.Evaluate(wrongTenant, context, options).ErrorCode);
        Assert.Equal("CNPG_CLUSTER_SPEC_MISMATCH", CloudNativePgClusterEvaluator.Evaluate(wrongStorage, context, options).ErrorCode);
        Assert.Equal("CNPG_CLUSTER_SPEC_MISMATCH", CloudNativePgClusterEvaluator.Evaluate(wrongInstances, context, options).ErrorCode);
        Assert.Equal("CNPG_CLUSTER_DELETING", CloudNativePgClusterEvaluator.Evaluate(deleting, context, options).ErrorCode);
        Assert.Equal("CNPG_CLUSTER_UNRECOVERABLE", CloudNativePgClusterEvaluator.Evaluate(unrecoverable, context, options).ErrorCode);
    }

    [Fact]
    public void Evaluator_AcceptsNormalizedResourceUnitsAndStrictReadyConditions()
    {
        var options = new CloudNativePgOptions();
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);

        var normalizedReady = BuildSnapshot(
            options,
            context,
            cpuRequest: "0.1",
            memoryLimit: "786432Ki",
            readyConditionObservedGeneration: 2,
            generation: 2);

        var eval = CloudNativePgClusterEvaluator.Evaluate(normalizedReady, context, options);
        Assert.Equal(CloudNativePgClusterEvaluationKind.Ready, eval.Kind);
        Assert.NotNull(eval.TechnicalReference);

        var staleGeneration = BuildSnapshot(
            options,
            context,
            generation: 3,
            readyConditionObservedGeneration: 2);
        Assert.Equal(CloudNativePgClusterEvaluationKind.Reconciling, CloudNativePgClusterEvaluator.Evaluate(staleGeneration, context, options).Kind);
    }

    [Fact]
    public async Task Executor_CreatesOnceWaitsUntilReadyAndStopsAtServiceDatabases()
    {
        var options = CreateFastOptions();
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);
        var reconciling = BuildSnapshot(options, context, readyInstances: 0, phase: "Setting up primary", readyConditionStatus: "False");
        var ready = BuildSnapshot(options, context);

        var getSequence = new Queue<CloudNativePgGetResult>(
        [
            CloudNativePgGetResult.Missing(),
            CloudNativePgGetResult.FoundCluster(reconciling),
            CloudNativePgGetResult.FoundCluster(ready)
        ]);
        var client = new StubCloudNativePgClusterClient(
            () => getSequence.Dequeue(),
            () => CloudNativePgCreateResult.CreatedCluster(reconciling));

        var executor = new CloudNativePgProvisioningStageExecutor(
            client,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var result = await executor.ExecuteStageAsync(context, CancellationToken.None);

        Assert.Equal(ProvisioningStageOutcomeKind.StageVerified, result.Outcome);
        Assert.Equal("saas-tenant-databases/pg-11111111222233334444555555555555@99999999-8888-7777-6666-555555555555", result.TechnicalReference);
        Assert.Equal(1, client.CreateCalls);
        Assert.Equal(3, client.GetCalls);

        var nextStageResult = await executor.ExecuteStageAsync(
            CreateStageContext(ProvisioningStage.ServiceDatabases),
            CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.AdapterNotConfigured, nextStageResult.Outcome);
    }

    [Fact]
    public async Task Executor_HandlesAlreadyExistsConflictAndTimeoutRecoveryWithoutDuplicateCreate()
    {
        var options = CreateFastOptions();
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);
        var ready = BuildSnapshot(options, context);

        var getAfterConflict = new Queue<CloudNativePgGetResult>(
        [
            CloudNativePgGetResult.Missing(),
            CloudNativePgGetResult.FoundCluster(ready)
        ]);
        var conflictClient = new StubCloudNativePgClusterClient(
            () => getAfterConflict.Dequeue(),
            () => CloudNativePgCreateResult.ConflictAlreadyExists());

        var executor = new CloudNativePgProvisioningStageExecutor(
            conflictClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var conflictResult = await executor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.StageVerified, conflictResult.Outcome);
        Assert.Equal(1, conflictClient.CreateCalls);
        Assert.Equal(2, conflictClient.GetCalls);

        // Simulacion de timeout incierto en CREATE seguido de recuperacion con RequiresVerification=true
        var timeoutClient = new StubCloudNativePgClusterClient(
            () => CloudNativePgGetResult.Missing(),
            () => CloudNativePgCreateResult.Uncertain("CNPG_CREATE_TIMEOUT_UNCERTAIN", "Timed out"));
        var timeoutExecutor = new CloudNativePgProvisioningStageExecutor(
            timeoutClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var uncertainResult = await timeoutExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.UncertainTimeout, uncertainResult.Outcome);
        Assert.Equal("CNPG_CREATE_TIMEOUT_UNCERTAIN", uncertainResult.ErrorCode);

        var adoptClient = new StubCloudNativePgClusterClient(
            () => CloudNativePgGetResult.FoundCluster(ready),
            () => throw new InvalidOperationException("Create must not be called during recovery of existing cluster."));
        var adoptExecutor = new CloudNativePgProvisioningStageExecutor(
            adoptClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var recovered = await adoptExecutor.ExecuteStageAsync(
            context with { RequiresVerification = true },
            CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.StageVerified, recovered.Outcome);
        Assert.Equal(0, adoptClient.CreateCalls);
    }

    [Fact]
    public async Task Executor_HandlesRbacErrorsReadinessWaitExpirationAndCancellation()
    {
        var options = CreateFastOptions();
        options.ReadinessTimeoutSeconds = 1;
        options.ReadinessPollIntervalSeconds = 1;
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);

        var rbacClient = new StubCloudNativePgClusterClient(
            () => CloudNativePgGetResult.Terminal("CNPG_RBAC_FORBIDDEN", "Forbidden"),
            () => CloudNativePgCreateResult.Terminal("CNPG_RBAC_FORBIDDEN", "Forbidden"));
        var rbacExecutor = new CloudNativePgProvisioningStageExecutor(
            rbacClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var terminal = await rbacExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, terminal.Outcome);
        Assert.Equal("CNPG_RBAC_FORBIDDEN", terminal.ErrorCode);

        var reconciling = BuildSnapshot(options, context, readyInstances: 0, phase: "Waiting for primary", readyConditionStatus: "False");
        var waitClient = new StubCloudNativePgClusterClient(
            () => CloudNativePgGetResult.FoundCluster(reconciling),
            () => CloudNativePgCreateResult.CreatedCluster(reconciling));
        var waitExecutor = new CloudNativePgProvisioningStageExecutor(
            waitClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var waitOutcome = await waitExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TransientFailure, waitOutcome.Outcome);
        Assert.Equal("CNPG_WAIT", waitOutcome.ErrorCode);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waitExecutor.ExecuteStageAsync(context, cts.Token));
    }

    [Fact]
    public void HostComposition_SeparatesApiFromWorkerAndFailsExplicitlyOutsideKubernetes()
    {
        using var apiApp = PlatformHostFactory.CreateApiApplication(
            [
                "--ConnectionStrings:PlatformDatabase=Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic"
            ]);
        Assert.Null(apiApp.Services.GetService<ICloudNativePgClusterClient>());
        Assert.Null(apiApp.Services.GetService<IProvisioningStageExecutor>());

        using var defaultWorkerHost = PlatformHostFactory.CreateWorkerHost(
            [
                "--ConnectionStrings:PlatformDatabase=Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic"
            ]);
        var defaultExecutor = defaultWorkerHost.Services.GetRequiredService<IProvisioningStageExecutor>();
        Assert.IsType<UnconfiguredProvisioningStageExecutor>(defaultExecutor);
        Assert.False(defaultExecutor.SupportsRealProvisioning);

        using var cnpgWorkerHost = PlatformHostFactory.CreateWorkerHost(
            [
                "--ConnectionStrings:PlatformDatabase=Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic",
                "--ProvisioningWorker:Enabled=true",
                "--ProvisioningWorker:Mode=CloudNativePg"
            ]);
        var cnpgExecutor = cnpgWorkerHost.Services.GetRequiredService<IProvisioningStageExecutor>();
        Assert.True(cnpgExecutor.SupportsRealProvisioning);
        Assert.Throws<InvalidOperationException>(() => cnpgExecutor.ValidateStartupConfiguration());
    }

    [Theory]
    [InlineData("CNPG_WAIT")]
    [InlineData("CNPG_API_UNAVAILABLE")]
    [InlineData("CNPG_CREATE_TIMEOUT_UNCERTAIN")]
    [InlineData("CNPG_RBAC_FORBIDDEN")]
    [InlineData("CNPG_CLUSTER_OWNERSHIP_CONFLICT")]
    [InlineData("CNPG_CLUSTER_SPEC_MISMATCH")]
    [InlineData("CNPG_CLUSTER_UNRECOVERABLE")]
    [InlineData("INVALID_TECHNICAL_REFERENCE")]
    [InlineData("ADAPTER_NOT_CONFIGURED")]
    public void ErrorCatalog_MapsAllCnpgErrorCodesToControlledMessages(string errorCode)
    {
        var summary = ProvisioningAdministrativeErrorCatalog.ResolveControlledSummary(
            errorCode,
            ProvisioningOperationStatus.FailedRetryable,
            manualInterventionRequired: false);

        Assert.False(string.IsNullOrWhiteSpace(summary));
    }

    [Fact]
    public void ClusterSnapshot_TryParse_ParsesValidCnpgV1302JsonAndRejectsMalformedPayloads()
    {
        const string validJson = """
            {
              "apiVersion": "postgresql.cnpg.io/v1",
              "kind": "Cluster",
              "metadata": {
                "name": "pg-11111111222233334444555555555555",
                "namespace": "saas-tenant-databases",
                "uid": "99999999-8888-7777-6666-555555555555",
                "generation": 2,
                "labels": {
                  "app.kubernetes.io/managed-by": "saas-platform",
                  "vital.saas/tenant-id": "11111111-2222-3333-4444-555555555555"
                }
              },
              "spec": {
                "instances": 1,
                "imageName": "ghcr.io/cloudnative-pg/postgresql:17.12-system-trixie@sha256:0f34d3fb12f415713ec94c3f2aa8295c7975f7114a0a8d360f26c958253bd032",
                "storage": {
                  "storageClass": "local-path",
                  "size": "2Gi"
                },
                "resources": {
                  "requests": { "cpu": "100m", "memory": "256Mi" },
                  "limits": { "cpu": "750m", "memory": "768Mi" }
                }
              },
              "status": {
                "instances": 1,
                "readyInstances": 1,
                "currentPrimary": "pg-11111111222233334444555555555555-1",
                "targetPrimary": "pg-11111111222233334444555555555555-1",
                "phase": "Cluster in healthy state",
                "conditions": [
                  {
                    "type": "ContinuousArchiving",
                    "status": "True",
                    "observedGeneration": 2
                  },
                  {
                    "type": "Ready",
                    "status": "True",
                    "reason": "ClusterIsReady",
                    "observedGeneration": 2
                  }
                ]
              }
            }
            """;

        using var doc = JsonDocument.Parse(validJson);
        Assert.True(CloudNativePgClusterSnapshot.TryParse(doc.RootElement, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal("99999999-8888-7777-6666-555555555555", snapshot.Uid);
        Assert.Equal(2L, snapshot.Generation);
        Assert.Equal(1, snapshot.Instances);
        Assert.Equal("local-path", snapshot.StorageClass);
        Assert.Equal("2Gi", snapshot.StorageSize);
        Assert.Equal("100m", snapshot.CpuRequest);
        Assert.Equal("750m", snapshot.CpuLimit);
        Assert.Equal("256Mi", snapshot.MemoryRequest);
        Assert.Equal("768Mi", snapshot.MemoryLimit);
        Assert.Equal(1, snapshot.StatusInstances);
        Assert.Equal(1, snapshot.ReadyInstances);
        Assert.Equal("pg-11111111222233334444555555555555-1", snapshot.CurrentPrimary);
        Assert.Equal("Cluster in healthy state", snapshot.Phase);
        Assert.Equal("True", snapshot.ReadyConditionStatus);
        Assert.Equal(2L, snapshot.ReadyConditionObservedGeneration);

        using var missingMetadata = JsonDocument.Parse("""{"spec":{"instances":1}}""");
        Assert.False(CloudNativePgClusterSnapshot.TryParse(missingMetadata.RootElement, out _));

        using var nonObject = JsonDocument.Parse("[]");
        Assert.False(CloudNativePgClusterSnapshot.TryParse(nonObject.RootElement, out _));
    }

    [Fact]
    public void KubernetesClient_MapsHttpStatusCodesConservativelyWithoutLeakingBodies()
    {
        Assert.Equal(CloudNativePgGetStatus.NotFound, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.NotFound).Status);
        Assert.Equal(CloudNativePgGetStatus.TimeoutUncertain, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.RequestTimeout).Status);
        Assert.Equal(CloudNativePgGetStatus.TimeoutUncertain, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.GatewayTimeout).Status);
        Assert.Equal(CloudNativePgGetStatus.TransientError, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.TooManyRequests).Status);
        Assert.Equal(CloudNativePgGetStatus.TransientError, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.InternalServerError).Status);
        Assert.Equal(CloudNativePgGetStatus.TransientError, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.BadGateway).Status);
        Assert.Equal(CloudNativePgGetStatus.TransientError, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.ServiceUnavailable).Status);
        Assert.Equal(CloudNativePgGetStatus.TerminalError, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.Unauthorized).Status);
        Assert.Equal(CloudNativePgGetStatus.TerminalError, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.Forbidden).Status);
        Assert.Equal(CloudNativePgGetStatus.TerminalError, KubernetesCloudNativePgClusterClient.MapGetHttpStatus(HttpStatusCode.BadRequest).Status);

        Assert.Equal(CloudNativePgCreateStatus.AlreadyExists, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.Conflict).Status);
        Assert.Equal(CloudNativePgCreateStatus.TimeoutUncertain, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.RequestTimeout).Status);
        Assert.Equal(CloudNativePgCreateStatus.TimeoutUncertain, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.GatewayTimeout).Status);
        Assert.Equal(CloudNativePgCreateStatus.TransientError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.TooManyRequests).Status);
        Assert.Equal(CloudNativePgCreateStatus.TransientError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.InternalServerError).Status);
        Assert.Equal(CloudNativePgCreateStatus.TransientError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.BadGateway).Status);
        Assert.Equal(CloudNativePgCreateStatus.TransientError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.ServiceUnavailable).Status);
        Assert.Equal(CloudNativePgCreateStatus.TerminalError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.Unauthorized).Status);
        Assert.Equal(CloudNativePgCreateStatus.TerminalError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.Forbidden).Status);
        Assert.Equal(CloudNativePgCreateStatus.TerminalError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.BadRequest).Status);
        Assert.Equal(CloudNativePgCreateStatus.TerminalError, KubernetesCloudNativePgClusterClient.MapCreateHttpStatus(HttpStatusCode.UnprocessableEntity).Status);
    }

    [Fact]
    public void Evaluator_RequiresExactProvisioningOperationIdLabel()
    {
        var options = new CloudNativePgOptions();
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);

        var missingOperationLabel = BuildSnapshot(options, context, includeOperationLabel: false);
        var missingEval = CloudNativePgClusterEvaluator.Evaluate(missingOperationLabel, context, options);
        Assert.Equal(CloudNativePgClusterEvaluationKind.TerminalConflict, missingEval.Kind);
        Assert.Equal("CNPG_CLUSTER_OWNERSHIP_CONFLICT", missingEval.ErrorCode);

        var mismatchedOperationLabel = BuildSnapshot(options, context, operationLabel: Guid.NewGuid().ToString("D"));
        var mismatchEval = CloudNativePgClusterEvaluator.Evaluate(mismatchedOperationLabel, context, options);
        Assert.Equal(CloudNativePgClusterEvaluationKind.TerminalConflict, mismatchEval.Kind);
        Assert.Equal("CNPG_CLUSTER_OWNERSHIP_CONFLICT", mismatchEval.ErrorCode);

        var matchingOperationLabel = BuildSnapshot(options, context);
        var validEval = CloudNativePgClusterEvaluator.Evaluate(matchingOperationLabel, context, options);
        Assert.Equal(CloudNativePgClusterEvaluationKind.Ready, validEval.Kind);
        Assert.Equal("saas-tenant-databases/pg-11111111222233334444555555555555@99999999-8888-7777-6666-555555555555", validEval.TechnicalReference);
    }

    [Fact]
    public async Task Executor_StopsWithTerminalFailureWhenClusterUidChangesDuringReconciliation()
    {
        var options = CreateFastOptions();
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);
        const string uidA = "11111111-aaaa-1111-aaaa-111111111111";
        const string uidB = "22222222-bbbb-2222-bbbb-222222222222";

        var reconcilingUidA = BuildSnapshot(options, context, uid: uidA, readyInstances: 0, phase: "Setting up primary", readyConditionStatus: "False");
        var readyUidB = BuildSnapshot(options, context, uid: uidB);

        // Escenario A: Cluster preexistente en reconciliacion cambia de UID en el siguiente sondeo
        var getQueue = new Queue<CloudNativePgGetResult>(
        [
            CloudNativePgGetResult.FoundCluster(reconcilingUidA),
            CloudNativePgGetResult.FoundCluster(readyUidB)
        ]);
        var adoptClient = new StubCloudNativePgClusterClient(
            () => getQueue.Dequeue(),
            () => throw new InvalidOperationException("Create not expected."));
        var adoptExecutor = new CloudNativePgProvisioningStageExecutor(
            adoptClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var adoptOutcome = await adoptExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, adoptOutcome.Outcome);
        Assert.Equal("CNPG_CLUSTER_IDENTITY_MISMATCH", adoptOutcome.ErrorCode);
        Assert.Equal(0, adoptClient.CreateCalls);

        // Escenario B: CREATE devuelve UID A y durante espera aparece UID B
        var responses = new Queue<CloudNativePgGetResult>(
        [
            CloudNativePgGetResult.Missing(),
            CloudNativePgGetResult.FoundCluster(readyUidB)
        ]);
        var createClient = new StubCloudNativePgClusterClient(
            () => responses.Dequeue(),
            () => CloudNativePgCreateResult.CreatedCluster(reconcilingUidA));

        var createExecutor = new CloudNativePgProvisioningStageExecutor(
            createClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var createOutcome = await createExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, createOutcome.Outcome);
        Assert.Equal("CNPG_CLUSTER_IDENTITY_MISMATCH", createOutcome.ErrorCode);
        Assert.Equal(1, createClient.CreateCalls);
    }

    [Fact]
    public async Task Executor_DoesNotStartNewPollAfterReadinessWindowExpires()
    {
        var context = CreateStageContext(ProvisioningStage.InfrastructureInstance);
        var options = new CloudNativePgOptions
        {
            ApiCallTimeoutSeconds = 2,
            ReadinessTimeoutSeconds = 1,
            ReadinessPollIntervalSeconds = 1
        };
        var reconciling = BuildSnapshot(options, context, readyInstances: 0, phase: "Setting up primary", readyConditionStatus: "False");

        // Escenario 1: Con Timeout=1s y PollInterval=1s, tras esperar el intervalo vence la ventana sin iniciar otro GET (ni spin por jitter)
        var strictClient = new StubCloudNativePgClusterClient(
            () => CloudNativePgGetResult.FoundCluster(reconciling),
            () => throw new InvalidOperationException("Create not expected."));
        var strictExecutor = new CloudNativePgProvisioningStageExecutor(
            strictClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var strictResult = await strictExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TransientFailure, strictResult.Outcome);
        Assert.Equal("CNPG_WAIT", strictResult.ErrorCode);
        Assert.Equal(1, strictClient.GetCalls);

        // Escenario 2: Si el reloj ya alcanzo el limite al concluir el GET inicial, tampoco espera ni emite otro GET
        var expiredClock = new TestAdvanceTimeProvider(new DateTimeOffset(2026, 11, 25, 13, 55, 0, TimeSpan.Zero));
        var expiredClient = new StubCloudNativePgClusterClient(
            () =>
            {
                expiredClock.Advance(TimeSpan.FromSeconds(2));
                return CloudNativePgGetResult.FoundCluster(reconciling);
            },
            () => throw new InvalidOperationException("Create not expected."));
        var expiredExecutor = new CloudNativePgProvisioningStageExecutor(
            expiredClient,
            options,
            expiredClock,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var expiredResult = await expiredExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TransientFailure, expiredResult.Outcome);
        Assert.Equal("CNPG_WAIT", expiredResult.ErrorCode);
        Assert.Equal(1, expiredClient.GetCalls);

        // Escenario 3: Si el reloj avanza mas alla del limite durante un poll previo, tampoco inicia otro GET
        var clock = new TestAdvanceTimeProvider(new DateTimeOffset(2026, 11, 25, 14, 0, 0, TimeSpan.Zero));
        var twoStepOptions = new CloudNativePgOptions
        {
            ApiCallTimeoutSeconds = 2,
            ReadinessTimeoutSeconds = 2,
            ReadinessPollIntervalSeconds = 1
        };
        var pollCount = 0;
        var windowClient = new StubCloudNativePgClusterClient(
            () =>
            {
                pollCount++;
                if (pollCount == 2)
                {
                    clock.Advance(TimeSpan.FromSeconds(5));
                }

                return CloudNativePgGetResult.FoundCluster(reconciling);
            },
            () => throw new InvalidOperationException("Create not expected."));
        var windowExecutor = new CloudNativePgProvisioningStageExecutor(
            windowClient,
            twoStepOptions,
            clock,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance);

        var windowResult = await windowExecutor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TransientFailure, windowResult.Outcome);
        Assert.Equal("CNPG_WAIT", windowResult.ErrorCode);
        Assert.Equal(2, windowClient.GetCalls);
    }

    private static CloudNativePgOptions CreateFastOptions()
        => new()
        {
            ApiCallTimeoutSeconds = 2,
            ReadinessTimeoutSeconds = 5,
            ReadinessPollIntervalSeconds = 1
        };

    private static ProvisioningStageContext CreateStageContext(ProvisioningStage stage)
        => new(
            SyntheticOperationId,
            SyntheticTenantId,
            "cln_synthetic01",
            "Clinica Demo",
            "req-cnpg-01",
            "Ana Demo",
            "admin.demo@synthetic.test",
            stage,
            RequiresVerification: false,
            AttemptCount: 1,
            InfrastructureResourceRef: null,
            ClinicalMigrationVersion: null,
            InitialAdminAccountId: null);

    private static CloudNativePgClusterSnapshot BuildSnapshot(
        CloudNativePgOptions options,
        ProvisioningStageContext context,
        string? managedBy = CloudNativePgClusterSpec.ManagedByLabelValue,
        string? tenantLabel = null,
        string? operationLabel = null,
        bool includeOperationLabel = true,
        string? uid = null,
        int? instances = null,
        string? storageSize = null,
        string? cpuRequest = null,
        string? memoryLimit = null,
        int? readyInstances = 1,
        string? phase = "Cluster in healthy state",
        string? readyConditionStatus = "True",
        long? generation = 1,
        long? readyConditionObservedGeneration = 1,
        string? deletionTimestamp = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (managedBy is not null)
        {
            labels[CloudNativePgClusterSpec.ManagedByLabelKey] = managedBy;
        }

        labels[CloudNativePgClusterSpec.TenantIdLabelKey] = tenantLabel ?? context.TenantId.ToString("D");
        if (includeOperationLabel)
        {
            labels[CloudNativePgClusterSpec.OperationIdLabelKey] = operationLabel ?? context.OperationId.ToString("D");
        }

        return new CloudNativePgClusterSnapshot(
            CloudNativePgClusterSpec.BuildClusterName(context.TenantId),
            options.Namespace,
            uid ?? "99999999-8888-7777-6666-555555555555",
            generation,
            deletionTimestamp,
            labels,
            instances ?? options.Instances,
            options.ImageName,
            options.StorageClass,
            storageSize ?? options.StorageSize,
            cpuRequest ?? options.CpuRequest,
            options.CpuLimit,
            options.MemoryRequest,
            memoryLimit ?? options.MemoryLimit,
            1,
            readyInstances,
            "pg-primary-1",
            "pg-primary-1",
            phase,
            readyConditionStatus,
            "ClusterIsReady",
            readyConditionObservedGeneration);
    }

    private sealed class StubCloudNativePgClusterClient(
        Func<CloudNativePgGetResult> onGet,
        Func<CloudNativePgCreateResult> onCreate) : ICloudNativePgClusterClient
    {
        public int GetCalls { get; private set; }

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
            GetCalls++;
            return Task.FromResult(onGet());
        }

        public Task<CloudNativePgCreateResult> CreateClusterAsync(
            string @namespace,
            IReadOnlyDictionary<string, object?> manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            return Task.FromResult(onCreate());
        }
    }

    private sealed class TestAdvanceTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
