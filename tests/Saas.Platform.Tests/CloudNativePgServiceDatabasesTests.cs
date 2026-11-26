using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saas.Platform.Api;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.AdminHttp;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class CloudNativePgServiceDatabasesTests
{
    private static readonly Guid TenantId = Guid.Parse("01a214af-fddc-713e-a908-c1f690486d90");
    private static readonly Guid OperationId = Guid.Parse("01a214af-fddc-75c8-9c5e-7f2317a4e6c2");
    private const string ClusterUid = "84fb9b6c-b40a-4c85-b822-810907498f73";

    [Fact]
    public void ServiceDatabasesOptions_ValidatesDistributionSecretsAndPreservesApiIsolation()
    {
        var valid = CreateEnabledOptions();
        valid.Validate();

        var missingIdentitySecret = CreateEnabledOptions();
        missingIdentitySecret.ServiceDatabases.IdentityDistributionSecret.Name = string.Empty;
        Assert.Throws<InvalidOperationException>(() => missingIdentitySecret.Validate());

        var duplicateSecret = CreateEnabledOptions();
        duplicateSecret.ServiceDatabases.ClinicalDistributionSecret.Namespace = duplicateSecret.ServiceDatabases.IdentityDistributionSecret.Namespace;
        duplicateSecret.ServiceDatabases.ClinicalDistributionSecret.Name = duplicateSecret.ServiceDatabases.IdentityDistributionSecret.Name;
        Assert.Throws<InvalidOperationException>(() => duplicateSecret.Validate());

        var authCollision = CreateEnabledOptions();
        authCollision.ServiceDatabases.IdentityDistributionSecret.Namespace = authCollision.Namespace;
        authCollision.ServiceDatabases.IdentityDistributionSecret.Name = "pg-auth-collision";
        Assert.Throws<InvalidOperationException>(() => authCollision.Validate());

        using var apiApp = PlatformHostFactory.CreateApiApplication(
            ["--ConnectionStrings:PlatformDatabase=Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic"]);
        Assert.Null(apiApp.Services.GetService<ICloudNativePgServiceDatabaseResourcesClient>());
        Assert.Null(apiApp.Services.GetService<IServiceDatabaseSqlVerifier>());
        Assert.Null(apiApp.Services.GetService<ResumeProvisioningOperationHandler>());
    }

    [Fact]
    public void TargetAndClusterSpec_BuildDeterministicNamesManifestsAndConnections()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);

        Assert.Equal($"role-{TenantId:N}-identity", ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId));
        Assert.Equal($"role-{TenantId:N}-clinical", ServiceDatabaseTarget.Clinical.BuildRoleResourceName(TenantId));
        Assert.Equal($"db-{TenantId:N}-identity", ServiceDatabaseTarget.Identity.BuildDatabaseResourceName(TenantId));
        Assert.Equal($"db-{TenantId:N}-clinical", ServiceDatabaseTarget.Clinical.BuildDatabaseResourceName(TenantId));
        Assert.Equal($"pg-auth-{TenantId:N}-identity", ServiceDatabaseTarget.Identity.BuildAuthSecretName(TenantId));
        Assert.Equal($"pg-auth-{TenantId:N}-clinical", ServiceDatabaseTarget.Clinical.BuildAuthSecretName(TenantId));
        Assert.Equal($"{TenantId:D}.connection", ServiceDatabaseTarget.BuildDistributionEntryKey(TenantId));

        var clusterJson = JsonSerializer.Serialize(CloudNativePgClusterSpec.BuildClusterManifest(context, options));
        foreach (var rule in CloudNativePgClusterSpec.RequiredServicePgHbaRules)
        {
            Assert.Contains(rule, clusterJson, StringComparison.Ordinal);
        }

        var defaultOptions = new CloudNativePgOptions();
        var defaultClusterJson = JsonSerializer.Serialize(CloudNativePgClusterSpec.BuildClusterManifest(context, defaultOptions));
        Assert.DoesNotContain("pg_hba", defaultClusterJson, StringComparison.Ordinal);

        var initialRoleJson = JsonSerializer.Serialize(ServiceDatabaseTarget.BuildDatabaseRoleManifest(
            context,
            options,
            ServiceDatabaseTarget.Identity,
            CloudNativePgClusterSpec.BuildClusterName(TenantId),
            loginEnabled: false));
        Assert.Contains("\"login\":false", initialRoleJson, StringComparison.Ordinal);
        Assert.Contains("\"superuser\":false", initialRoleJson, StringComparison.Ordinal);
        Assert.Contains("\"databaseRoleReclaimPolicy\":\"retain\"", initialRoleJson, StringComparison.Ordinal);

        var passwordA = ServiceDatabaseTarget.GenerateSecurePassword();
        var passwordB = ServiceDatabaseTarget.GenerateSecurePassword();
        Assert.NotEqual(passwordA, passwordB);
        Assert.True(passwordA.Length >= 32);

        var conn = ServiceDatabaseTarget.BuildServiceConnectionString(
            "pg-test-rw.saas-tenant-databases.svc",
            5432,
            ServiceDatabaseTarget.Identity,
            passwordA);
        var parsed = new NpgsqlConnectionStringBuilder(conn);
        Assert.Equal("pg-test-rw.saas-tenant-databases.svc", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("identity", parsed.Database);
        Assert.Equal("identity_owner", parsed.Username);
        Assert.Equal(SslMode.Require, parsed.SslMode);
        Assert.True(ServiceDatabaseTarget.AreConnectionStringsEquivalent(conn, conn));
    }

    [Fact]
    public void ClusterEvaluator_ForServiceDatabasesRejectsUidMismatchAndMissingOrUnsafePgHba()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);

        var validCluster = BuildReadyCluster(options, context, ClusterUid, CloudNativePgClusterSpec.RequiredServicePgHbaRules);
        Assert.Equal(
            CloudNativePgClusterEvaluationKind.Ready,
            CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(validCluster, context, options, ClusterUid).Kind);

        var uidMismatch = CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(validCluster, context, options, Guid.NewGuid().ToString("D"));
        Assert.Equal("CNPG_CLUSTER_IDENTITY_MISMATCH", uidMismatch.ErrorCode);

        var noPgHbaCluster = BuildReadyCluster(options, context, ClusterUid, []);
        var missingHbaEval = CloudNativePgClusterEvaluator.Evaluate(noPgHbaCluster, context, options);
        Assert.Equal("CNPG_PGHBA_NOT_CONFIGURED", missingHbaEval.ErrorCode);

        var misorderedHba = BuildReadyCluster(
            new CloudNativePgOptions(),
            context,
            ClusterUid,
            [
                "host all identity_owner,clinical_owner all reject",
                "hostssl identity identity_owner all scram-sha-256",
                "hostssl clinical clinical_owner all scram-sha-256"
            ]);
        Assert.Equal(
            "CNPG_PGHBA_NOT_CONFIGURED",
            CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(misorderedHba, context, options, ClusterUid).ErrorCode);

        var permissiveBeforeReject = BuildReadyCluster(
            new CloudNativePgOptions(),
            context,
            ClusterUid,
            [
                "hostssl identity identity_owner all scram-sha-256",
                "host all all all trust",
                "hostssl clinical clinical_owner all scram-sha-256",
                "host all identity_owner,clinical_owner all reject"
            ]);
        Assert.Equal(
            "CNPG_PGHBA_NOT_CONFIGURED",
            CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(permissiveBeforeReject, context, options, ClusterUid).ErrorCode);

        var permissiveSpecificAddressRule = BuildReadyCluster(
            new CloudNativePgOptions(),
            context,
            ClusterUid,
            [
                "hostssl identity identity_owner all scram-sha-256",
                "hostssl clinical identity_owner 10.42.0.0/16 scram-sha-256",
                "hostssl clinical clinical_owner all scram-sha-256",
                "host all identity_owner,clinical_owner all reject"
            ]);
        Assert.Equal(
            "CNPG_PGHBA_NOT_CONFIGURED",
            CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(permissiveSpecificAddressRule, context, options, ClusterUid).ErrorCode);

        var swappedAllowOrder = BuildReadyCluster(
            new CloudNativePgOptions(),
            context,
            ClusterUid,
            [
                "hostssl clinical clinical_owner all scram-sha-256",
                "hostssl identity identity_owner all scram-sha-256",
                "host all identity_owner,clinical_owner all reject"
            ]);
        Assert.Equal(
            "CNPG_PGHBA_NOT_CONFIGURED",
            CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(swappedAllowOrder, context, options, ClusterUid).ErrorCode);

        var extraRuleAfterReject = BuildReadyCluster(
            new CloudNativePgOptions(),
            context,
            ClusterUid,
            [
                "hostssl identity identity_owner all scram-sha-256",
                "hostssl clinical clinical_owner all scram-sha-256",
                "host all identity_owner,clinical_owner all reject",
                "hostssl app identity_owner 10.42.0.0/16 scram-sha-256"
            ]);
        Assert.Equal(
            "CNPG_PGHBA_NOT_CONFIGURED",
            CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(extraRuleAfterReject, context, options, ClusterUid).ErrorCode);
    }

    [Fact]
    public async Task StageExecutor_CompletesAllNineStepsStopsAtClinicalPreparationAndDistributesConnections()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterClient = new FakeClusterClient(BuildReadyCluster(options, context, ClusterUid, CloudNativePgClusterSpec.RequiredServicePgHbaRules));
        var resourceClient = new InMemoryServiceResourcesClient(options);
        var sqlVerifier = new RecordingSqlVerifier();

        var handler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            resourceClient,
            sqlVerifier,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var executor = new CloudNativePgProvisioningStageExecutor(
            clusterClient,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgProvisioningStageExecutor>.Instance,
            handler);

        Assert.True(executor.SupportsStage(ProvisioningStage.InfrastructureInstance));
        Assert.True(executor.SupportsStage(ProvisioningStage.ServiceDatabases));
        Assert.False(executor.SupportsStage(ProvisioningStage.ClinicalPreparation));
        Assert.False(executor.SupportsStage(ProvisioningStage.IdentityPreparation));

        var result = await executor.ExecuteStageAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.StageVerified, result.Outcome);

        Assert.True(sqlVerifier.PreLoginVerified);
        Assert.Equal(["identity", "clinical"], sqlVerifier.RevokedServices);
        Assert.True(sqlVerifier.PostLoginMatrixVerified);

        var entryKey = ServiceDatabaseTarget.BuildDistributionEntryKey(TenantId);
        Assert.True(resourceClient.Secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)]
            .Data.TryGetValue(entryKey, out var identityDsn));
        Assert.True(resourceClient.Secrets[(options.ServiceDatabases.ClinicalDistributionSecret.Namespace, options.ServiceDatabases.ClinicalDistributionSecret.Name)]
            .Data.TryGetValue(entryKey, out var clinicalDsn));

        Assert.Contains("Database=identity", identityDsn, StringComparison.Ordinal);
        Assert.Contains("Username=identity_owner", identityDsn, StringComparison.Ordinal);
        Assert.Contains("SSL Mode=Require", identityDsn, StringComparison.Ordinal);
        Assert.Contains("Database=clinical", clinicalDsn, StringComparison.Ordinal);
        Assert.Contains("Username=clinical_owner", clinicalDsn, StringComparison.Ordinal);

        // Etapas posteriores permanecen detenidas en AdapterNotConfigured
        var clinicalStageResult = await executor.ExecuteStageAsync(CreateContext(ProvisioningStage.ClinicalPreparation), CancellationToken.None);
        var identityStageResult = await executor.ExecuteStageAsync(CreateContext(ProvisioningStage.IdentityPreparation), CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.AdapterNotConfigured, clinicalStageResult.Outcome);
        Assert.Equal(ProvisioningStageOutcomeKind.AdapterNotConfigured, identityStageResult.Outcome);
    }

    [Fact]
    public async Task StageHandler_FailsOnStaleObservedGenerationOrConflictsWithoutDistributing()
    {
        var options = CreateEnabledOptions();
        options.ReadinessTimeoutSeconds = 1;
        options.ReadinessPollIntervalSeconds = 1;
        var context = CreateContext(ProvisioningStage.ServiceDatabases);

        // Caso A: DatabaseRole con generation=2 pero observedGeneration=1 -> espera expirada CNPG_ROLE_WAIT
        var clusterClient = new FakeClusterClient(BuildReadyCluster(options, context, ClusterUid, CloudNativePgClusterSpec.RequiredServicePgHbaRules));
        var staleClient = new InMemoryServiceResourcesClient(options)
        {
            ForceRoleGenerationMismatch = true
        };
        var handler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            staleClient,
            new RecordingSqlVerifier(),
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var staleResult = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TransientFailure, staleResult.Outcome);
        Assert.Equal("CNPG_ROLE_WAIT", staleResult.ErrorCode);
        Assert.False(staleClient.Secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)]
            .Data.ContainsKey(ServiceDatabaseTarget.BuildDistributionEntryKey(TenantId)));

        // Caso B: Rol preexistente con privilegios elevados o dueño incorrecto -> TerminalFailure
        var elevatedClient = new InMemoryServiceResourcesClient(options);
        var roleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId);
        elevatedClient.Roles[(options.Namespace, roleName)] = new CloudNativePgDatabaseRoleSnapshot(
            roleName,
            options.Namespace,
            "role-uid",
            "1",
            1,
            null,
            ServiceDatabaseTarget.BuildResourceLabels(context, ServiceDatabaseTarget.Identity),
            CloudNativePgClusterSpec.BuildClusterName(TenantId),
            "identity_owner",
            "present",
            "retain",
            false,
            Superuser: true,
            false,
            false,
            false,
            false,
            null,
            true,
            1,
            null);

        var conflictHandler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            elevatedClient,
            new RecordingSqlVerifier(),
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var conflictResult = await conflictHandler.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, conflictResult.Outcome);
        Assert.Equal("CNPG_ROLE_CONFLICT", conflictResult.ErrorCode);
    }

    [Fact]
    public async Task StageHandler_ReusesExistingAuthSecretBlocksLoginWhenPgHbaNotAppliedAndPreservesOtherTenants()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterClient = new FakeClusterClient(BuildReadyCluster(options, context, ClusterUid, CloudNativePgClusterSpec.RequiredServicePgHbaRules));
        var resourceClient = new InMemoryServiceResourcesClient(options);

        // Pre-seed de otra clinica en el Secret de distribucion
        var otherTenantKey = $"{Guid.NewGuid():D}.connection";
        const string otherTenantDsn = "Host=other-rw.saas-tenant-databases.svc;Database=identity;Username=identity_owner;Password=other;SSL Mode=Require";
        resourceClient.Secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)]
            .Data[otherTenantKey] = otherTenantDsn;

        // Simular fallo de pg_hba antes de habilitar LOGIN: no debe activar login ni regenerar el Secret
        var failingSql = new RecordingSqlVerifier
        {
            PreLoginResult = ServiceDatabaseSqlVerificationResult.Transient("CNPG_PGHBA_NOT_APPLIED", "pg_hba not active yet")
        };
        var handlerFail = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            resourceClient,
            failingSql,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var blocked = await handlerFail.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TransientFailure, blocked.Outcome);
        Assert.Equal("CNPG_PGHBA_NOT_APPLIED", blocked.ErrorCode);
        Assert.Equal(0, resourceClient.RoleReplaceCount);

        var preservedIdentitySecret = resourceClient.Secrets[(options.Namespace, ServiceDatabaseTarget.Identity.BuildAuthSecretName(TenantId))];
        var originalPassword = preservedIdentitySecret.Data[ServiceDatabaseTarget.AuthSecretPasswordKey];

        // Segunda corrida con pg_hba activo y conflicto 409 en el Secret de distribucion
        resourceClient.SimulateNextDistributionConflict = true;
        var recoveringSql = new RecordingSqlVerifier();
        var handlerRecover = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            resourceClient,
            recoveringSql,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var recovered = await handlerRecover.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.StageVerified, recovered.Outcome);

        var idSecretAfter = resourceClient.Secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)];
        Assert.Equal(otherTenantDsn, idSecretAfter.Data[otherTenantKey]);
        Assert.Contains($"Password={originalPassword}", idSecretAfter.Data[ServiceDatabaseTarget.BuildDistributionEntryKey(TenantId)], StringComparison.Ordinal);
        Assert.Equal(2, resourceClient.RoleReplaceCount); // identity + clinical habilitados tras pg_hba

        // Tercera corrida idempotente: no vuelve a deshabilitar roles activos ni falla con la entrada ya presente
        var replay = await handlerRecover.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.StageVerified, replay.Outcome);
        Assert.Equal(2, resourceClient.RoleReplaceCount);

        // Si existe entrada incompatible en el Secret de distribucion, rechaza sin sobrescribir
        idSecretAfter.Data[ServiceDatabaseTarget.BuildDistributionEntryKey(TenantId)] =
            "Host=wrong-host;Database=identity;Username=identity_owner;Password=mismatch;SSL Mode=Require";
        var conflict = await handlerRecover.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, conflict.Outcome);
        Assert.Equal("CNPG_DISTRIBUTION_SECRET_CONFLICT", conflict.ErrorCode);
        Assert.Contains("Password=mismatch", idSecretAfter.Data[ServiceDatabaseTarget.BuildDistributionEntryKey(TenantId)], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageHandler_RefusesToRegenerateCredentialsWhenRoleActiveOrConnectionDistributedOrPasswordsEqual()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterClient = new FakeClusterClient(BuildReadyCluster(options, context, ClusterUid, CloudNativePgClusterSpec.RequiredServicePgHbaRules));

        // 1. Rol ya activo/vinculado pero falta pg-auth-* -> CNPG_AUTH_SECRET_CONFLICT sin crear Secret ni mutar el rol
        var activeRoleClient = new InMemoryServiceResourcesClient(options);
        var idRoleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId);
        var idAuthSecretName = ServiceDatabaseTarget.Identity.BuildAuthSecretName(TenantId);
        activeRoleClient.Roles[(options.Namespace, idRoleName)] = new CloudNativePgDatabaseRoleSnapshot(
            idRoleName,
            options.Namespace,
            "role-uid",
            "2",
            2,
            null,
            ServiceDatabaseTarget.BuildResourceLabels(context, ServiceDatabaseTarget.Identity),
            CloudNativePgClusterSpec.BuildClusterName(TenantId),
            "identity_owner",
            "present",
            "retain",
            Login: true,
            false,
            false,
            false,
            false,
            false,
            idAuthSecretName,
            true,
            2,
            "1");

        var activeRoleHandler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            activeRoleClient,
            new RecordingSqlVerifier(),
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var activeRoleResult = await activeRoleHandler.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, activeRoleResult.Outcome);
        Assert.Equal("CNPG_AUTH_SECRET_CONFLICT", activeRoleResult.ErrorCode);
        Assert.Equal(0, activeRoleClient.AuthSecretCreateCount);
        Assert.Equal(0, activeRoleClient.RoleReplaceCount);

        // 2. Rol aun NOLOGIN pero ya existe conexion distribuida en Secret de consumidor -> CNPG_AUTH_SECRET_CONFLICT sin regenerar
        var distributedExistsClient = new InMemoryServiceResourcesClient(options);
        var idDistSecret = distributedExistsClient.Secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)];
        idDistSecret.Data[ServiceDatabaseTarget.BuildDistributionEntryKey(TenantId)] =
            "Host=pg-rw;Database=identity;Username=identity_owner;Password=already-issued;SSL Mode=Require";

        var distributedExistsHandler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            distributedExistsClient,
            new RecordingSqlVerifier(),
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var distributedResult = await distributedExistsHandler.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, distributedResult.Outcome);
        Assert.Equal("CNPG_AUTH_SECRET_CONFLICT", distributedResult.ErrorCode);
        Assert.Equal(0, distributedExistsClient.AuthSecretCreateCount);

        // 3. Dos Secrets existentes con la misma contraseña -> CNPG_AUTH_SECRET_CONFLICT por falta de independencia
        var identicalSecretsClient = new InMemoryServiceResourcesClient(options);
        await identicalSecretsClient.CreateBasicAuthSecretAsync(
            options.Namespace,
            ServiceDatabaseTarget.Identity.BuildAuthSecretName(TenantId),
            ServiceDatabaseTarget.BuildResourceLabels(context, ServiceDatabaseTarget.Identity, includeCnpgReload: true),
            ServiceDatabaseTarget.Identity.RoleName,
            "SharedPasswordNotAllowed!",
            CancellationToken.None);
        await identicalSecretsClient.CreateBasicAuthSecretAsync(
            options.Namespace,
            ServiceDatabaseTarget.Clinical.BuildAuthSecretName(TenantId),
            ServiceDatabaseTarget.BuildResourceLabels(context, ServiceDatabaseTarget.Clinical, includeCnpgReload: true),
            ServiceDatabaseTarget.Clinical.RoleName,
            "SharedPasswordNotAllowed!",
            CancellationToken.None);

        var identicalHandler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            identicalSecretsClient,
            new RecordingSqlVerifier(),
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var identicalResult = await identicalHandler.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, identicalResult.Outcome);
        Assert.Equal("CNPG_AUTH_SECRET_CONFLICT", identicalResult.ErrorCode);
        Assert.Equal(0, identicalSecretsClient.SecretReplaceCount);
    }

    [Theory]
    [InlineData("kubernetes.io/tls", null, null, null)]
    [InlineData("Opaque", "cluster-a", null, null)]
    [InlineData("Opaque", null, "wrong-purpose", null)]
    [InlineData("Opaque", null, null, "clinical")]
    public async Task StageHandler_RejectsInvalidOrMisidentifiedDistributionSecretsWithoutWriting(
        string secretType,
        string? cnpgClusterLabel,
        string? purposeOverride,
        string? serviceOverride)
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterClient = new FakeClusterClient(BuildReadyCluster(options, context, ClusterUid, CloudNativePgClusterSpec.RequiredServicePgHbaRules));
        var resourceClient = new InMemoryServiceResourcesClient(options);

        var idSecret = resourceClient.Secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)];
        idSecret.Type = secretType;
        if (cnpgClusterLabel is not null)
        {
            idSecret.Labels[ServiceDatabaseTarget.CnpgClusterLabelKey] = cnpgClusterLabel;
        }

        if (purposeOverride is not null)
        {
            idSecret.Labels[ServiceDatabaseTarget.PurposeLabelKey] = purposeOverride;
        }

        if (serviceOverride is not null)
        {
            idSecret.Labels[ServiceDatabaseTarget.ServiceLabelKey] = serviceOverride;
        }

        var handler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            resourceClient,
            new RecordingSqlVerifier(),
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var result = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.TerminalFailure, result.Outcome);
        Assert.Equal("CNPG_DISTRIBUTION_SECRET_CONFLICT", result.ErrorCode);
        Assert.Equal(0, resourceClient.AuthSecretCreateCount);
        Assert.Equal(0, resourceClient.SecretReplaceCount);
    }

    [Fact]
    public void DatabaseRoleSnapshot_NormalizesOmittedDefaultFalseBooleansWithoutWeakeningSecurityOrStatus()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterName = CloudNativePgClusterSpec.BuildClusterName(TenantId);
        var roleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId);

        // Fixture derivada de contrato CNPG v1.30.2 (RoleConfiguration con `omitempty` en los 6 bool false;
        // en saas-infra no existe volcado JSON crudo capturado del recurso real).
        var reconciledOmittingBooleansJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "eee4ce4c-f650-471a-8e59-01cc13ac0be9",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: null,
            applied: true,
            observedGeneration: 1);

        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(reconciledOmittingBooleansJson, out var omittedSnapshot));
        Assert.NotNull(omittedSnapshot);
        Assert.False(omittedSnapshot.Login);
        Assert.False(omittedSnapshot.Superuser);
        Assert.False(omittedSnapshot.Createdb);
        Assert.False(omittedSnapshot.Createrole);
        Assert.False(omittedSnapshot.Replication);
        Assert.False(omittedSnapshot.BypassRls);
        Assert.Equal(
            ServiceResourceEvaluationKind.Ready,
            omittedSnapshot.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: false).Kind);

        // Booleanos explícitos en false también son válidos
        var explicitFalseJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "eee4ce4c-f650-471a-8e59-01cc13ac0be9",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: """
                ,
                "login": false,
                "superuser": false,
                "createdb": false,
                "createrole": false,
                "replication": false,
                "bypassrls": false
                """,
            applied: true,
            observedGeneration: 1);

        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(explicitFalseJson, out var explicitFalseSnapshot));
        Assert.NotNull(explicitFalseSnapshot);
        Assert.Equal(
            ServiceResourceEvaluationKind.Ready,
            explicitFalseSnapshot.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: false).Kind);

        // login=true + passwordSecret válido con los 5 booleanos administrativos omitidos
        var authSecretName = ServiceDatabaseTarget.Identity.BuildAuthSecretName(TenantId);
        var activeLoginJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "eee4ce4c-f650-471a-8e59-01cc13ac0be9",
            generation: 2,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: $$"""
                ,
                "login": true,
                "passwordSecret": { "name": "{{authSecretName}}" }
                """,
            applied: true,
            observedGeneration: 2);

        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(activeLoginJson, out var activeSnapshot));
        Assert.NotNull(activeSnapshot);
        Assert.True(activeSnapshot.Login);
        Assert.False(activeSnapshot.Superuser);
        Assert.Equal(
            ServiceResourceEvaluationKind.Ready,
            activeSnapshot.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: true, authSecretName).Kind);
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("createdb")]
    [InlineData("createrole")]
    [InlineData("replication")]
    [InlineData("bypassrls")]
    public void DatabaseRoleSnapshot_RejectsElevatedPrivilegeBooleanWhenExplicitlyTrue(string privilegeField)
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterName = CloudNativePgClusterSpec.BuildClusterName(TenantId);
        var roleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId);

        var elevatedJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "role-elevated-uid",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: $$"""
                ,
                "{{privilegeField}}": true
                """,
            applied: true,
            observedGeneration: 1);

        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(elevatedJson, out var snapshot));
        var eval = snapshot!.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: false);
        Assert.Equal(ServiceResourceEvaluationKind.TerminalConflict, eval.Kind);
        Assert.Equal("CNPG_ROLE_CONFLICT", eval.ErrorCode);
    }

    [Theory]
    [InlineData("login", "null")]
    [InlineData("superuser", "null")]
    [InlineData("createdb", "\"false\"")]
    [InlineData("createrole", "0")]
    [InlineData("replication", "{}")]
    [InlineData("bypassrls", "[]")]
    public void DatabaseRoleSnapshot_FailsClosedOnExplicitNullOrInvalidBooleanTypes(string propertyName, string rawJsonValue)
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterName = CloudNativePgClusterSpec.BuildClusterName(TenantId);
        var roleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId);

        var malformedJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "role-malformed-uid",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: $$"""
                ,
                "{{propertyName}}": {{rawJsonValue}}
                """,
            applied: true,
            observedGeneration: 1);

        Assert.False(CloudNativePgDatabaseRoleSnapshot.TryParse(malformedJson, out var snapshot));
        Assert.Null(snapshot);
    }

    [Fact]
    public void DatabaseRoleSnapshot_KeepsStatusAppliedAndObservedGenerationUnnormalizedWhenMissingOrFalseOrStale()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterName = CloudNativePgClusterSpec.BuildClusterName(TenantId);
        var roleName = ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId);

        var missingAppliedJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "role-no-applied",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: null,
            applied: null,
            observedGeneration: 1);
        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(missingAppliedJson, out var missingApplied));
        Assert.Null(missingApplied!.Applied);
        Assert.Equal(
            ServiceResourceEvaluationKind.Reconciling,
            missingApplied.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: false).Kind);

        var falseAppliedJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "role-false-applied",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: null,
            applied: false,
            observedGeneration: 1);
        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(falseAppliedJson, out var falseApplied));
        Assert.False(falseApplied!.Applied);
        Assert.Equal(
            ServiceResourceEvaluationKind.Reconciling,
            falseApplied.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: false).Kind);

        var missingObservedGenJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "role-no-obsgen",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: null,
            applied: true,
            observedGeneration: null);
        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(missingObservedGenJson, out var missingObservedGen));
        Assert.Null(missingObservedGen!.ObservedGeneration);
        Assert.Equal(
            ServiceResourceEvaluationKind.Reconciling,
            missingObservedGen.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: false).Kind);

        var staleObservedGenJson = BuildDatabaseRoleJson(
            roleName,
            options.Namespace,
            "role-stale-obsgen",
            generation: 2,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: null,
            applied: true,
            observedGeneration: 1);
        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(staleObservedGenJson, out var staleObservedGen));
        Assert.Equal(
            ServiceResourceEvaluationKind.Reconciling,
            staleObservedGen!.Evaluate(context, options, ServiceDatabaseTarget.Identity, clusterName, requireLoginReady: false).Kind);
    }

    [Fact]
    public async Task StageHandler_ReusesPreExistingIdentityDatabaseRoleWithOmittedBooleansWithoutRecreatingAndCompletesStage()
    {
        var options = CreateEnabledOptions();
        var context = CreateContext(ProvisioningStage.ServiceDatabases);
        var clusterName = CloudNativePgClusterSpec.BuildClusterName(TenantId);
        var clusterClient = new FakeClusterClient(BuildReadyCluster(options, context, ClusterUid, CloudNativePgClusterSpec.RequiredServicePgHbaRules));
        var resourceClient = new InMemoryServiceResourcesClient(options);
        var sqlVerifier = new RecordingSqlVerifier();

        var preExistingIdentityRoleJson = BuildDatabaseRoleJson(
            ServiceDatabaseTarget.Identity.BuildRoleResourceName(TenantId),
            options.Namespace,
            "eee4ce4c-f650-471a-8e59-01cc13ac0be9",
            generation: 1,
            context,
            ServiceDatabaseTarget.Identity,
            clusterName,
            extraSpecProperties: null,
            applied: true,
            observedGeneration: 1);
        Assert.True(CloudNativePgDatabaseRoleSnapshot.TryParse(preExistingIdentityRoleJson, out var preExistingIdentityRole));
        resourceClient.Roles[(options.Namespace, preExistingIdentityRole!.Name)] = preExistingIdentityRole;

        var handler = new CloudNativePgServiceDatabasesStageHandler(
            clusterClient,
            resourceClient,
            sqlVerifier,
            options,
            TimeProvider.System,
            NullLogger<CloudNativePgServiceDatabasesStageHandler>.Instance);

        var result = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ProvisioningStageOutcomeKind.StageVerified, result.Outcome);

        // Identity se reutilizó sin CREATE; únicamente Clinical ejecutó CREATE de DatabaseRole.
        Assert.Equal([ServiceDatabaseTarget.Clinical.BuildRoleResourceName(TenantId)], resourceClient.CreatedRoleNames);
        Assert.Equal(2, resourceClient.RoleReplaceCount);
        Assert.Equal("eee4ce4c-f650-471a-8e59-01cc13ac0be9", resourceClient.Roles[(options.Namespace, preExistingIdentityRole.Name)].Uid);
        Assert.True(sqlVerifier.PreLoginVerified);
        Assert.Equal(["identity", "clinical"], sqlVerifier.RevokedServices);
        Assert.True(sqlVerifier.PostLoginMatrixVerified);
    }

    [Fact]
    public void HostFactory_ResolvesTemporaryRecoveryCliModeWithoutRegisteringInApiHost()
    {
        Assert.Equal(
            PlatformRuntimeMode.RecoverPlat05bSerialization,
            PlatformHostFactory.ResolveRuntimeMode(["--mode=recover-plat05b-serialization"]));

        Assert.Equal(
            PlatformRuntimeMode.ResumeOperation,
            PlatformHostFactory.ResolveRuntimeMode(["--mode=resume-operation"]));

        using var apiApp = PlatformHostFactory.CreateApiApplication(
            ["--ConnectionStrings:PlatformDatabase=Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic"]);
        Assert.Null(apiApp.Services.GetService<RecoverPlat05bSerializationIncidentHandler>());
    }

    [Theory]
    [InlineData("CNPG_PGHBA_NOT_APPLIED")]
    [InlineData("CNPG_ROLE_WAIT")]
    [InlineData("CNPG_DATABASE_WAIT")]
    [InlineData("CNPG_DISTRIBUTION_SECRET_MISSING")]
    [InlineData("CNPG_DISTRIBUTION_SECRET_CONFLICT")]
    [InlineData("PG_ACL_VERIFICATION_FAILED")]
    [InlineData("PG_INFRASTRUCTURE_UNREACHABLE")]
    public void ErrorCatalog_MapsAllPlat05bErrorCodes(string code)
    {
        var summary = ProvisioningAdministrativeErrorCatalog.ResolveControlledSummary(
            code,
            ProvisioningOperationStatus.FailedRetryable,
            manualInterventionRequired: false);

        Assert.False(string.IsNullOrWhiteSpace(summary));
    }

    private static CloudNativePgOptions CreateEnabledOptions()
        => new()
        {
            ApiCallTimeoutSeconds = 2,
            ReadinessTimeoutSeconds = 5,
            ReadinessPollIntervalSeconds = 1,
            ServiceDatabases = new CloudNativePgServiceDatabasesOptions
            {
                Enabled = true,
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

    private static ProvisioningStageContext CreateContext(ProvisioningStage stage)
        => new(
            OperationId,
            TenantId,
            "plat05b-lab",
            "Clinica Lab PLAT-05B",
            "req-plat05b-01",
            "Admin Lab",
            "admin@synthetic.test",
            stage,
            RequiresVerification: false,
            AttemptCount: 2,
            InfrastructureResourceRef: $"saas-tenant-databases/{CloudNativePgClusterSpec.BuildClusterName(TenantId)}@{ClusterUid}",
            ClinicalMigrationVersion: null,
            InitialAdminAccountId: null);

    private static CloudNativePgClusterSnapshot BuildReadyCluster(
        CloudNativePgOptions options,
        ProvisioningStageContext context,
        string uid,
        IReadOnlyList<string> pgHba)
        => new(
            CloudNativePgClusterSpec.BuildClusterName(context.TenantId),
            options.Namespace,
            uid,
            1,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CloudNativePgClusterSpec.ManagedByLabelKey] = CloudNativePgClusterSpec.ManagedByLabelValue,
                [CloudNativePgClusterSpec.TenantIdLabelKey] = context.TenantId.ToString("D"),
                [CloudNativePgClusterSpec.OperationIdLabelKey] = context.OperationId.ToString("D")
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
            $"{CloudNativePgClusterSpec.BuildClusterName(context.TenantId)}-1",
            $"{CloudNativePgClusterSpec.BuildClusterName(context.TenantId)}-1",
            "Cluster in healthy state",
            "True",
            "ClusterIsReady",
            1,
            pgHba,
            $"{CloudNativePgClusterSpec.BuildClusterName(context.TenantId)}-rw");

    private static JsonElement BuildDatabaseRoleJson(
        string name,
        string @namespace,
        string uid,
        long generation,
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        string clusterName,
        string? extraSpecProperties,
        bool? applied,
        long? observedGeneration)
    {
        var statusFields = new List<string>();
        if (applied.HasValue)
        {
            statusFields.Add($"\"applied\": {(applied.Value ? "true" : "false")}");
        }

        if (observedGeneration.HasValue)
        {
            statusFields.Add($"\"observedGeneration\": {observedGeneration.Value}");
        }

        var json = $$"""
            {
              "apiVersion": "postgresql.cnpg.io/v1",
              "kind": "DatabaseRole",
              "metadata": {
                "name": "{{name}}",
                "namespace": "{{@namespace}}",
                "uid": "{{uid}}",
                "resourceVersion": "{{generation}}",
                "generation": {{generation}},
                "labels": {
                  "{{CloudNativePgClusterSpec.ManagedByLabelKey}}": "{{CloudNativePgClusterSpec.ManagedByLabelValue}}",
                  "{{CloudNativePgClusterSpec.TenantIdLabelKey}}": "{{context.TenantId:D}}",
                  "{{CloudNativePgClusterSpec.OperationIdLabelKey}}": "{{context.OperationId:D}}",
                  "{{ServiceDatabaseTarget.ServiceLabelKey}}": "{{target.ServiceKey}}"
                }
              },
              "spec": {
                "cluster": { "name": "{{clusterName}}" },
                "name": "{{target.RoleName}}",
                "ensure": "present",
                "databaseRoleReclaimPolicy": "retain"{{extraSpecProperties ?? string.Empty}}
              },
              "status": {
                {{string.Join(", ", statusFields)}}
              }
            }
            """;

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private sealed class FakeClusterClient(CloudNativePgClusterSnapshot cluster) : ICloudNativePgClusterClient
    {
        public void EnsureInitialized() { }

        public Task<CloudNativePgGetResult> GetClusterAsync(string @namespace, string clusterName, CancellationToken cancellationToken)
            => Task.FromResult(CloudNativePgGetResult.FoundCluster(cluster));

        public Task<CloudNativePgCreateResult> CreateClusterAsync(string @namespace, IReadOnlyDictionary<string, object?> manifest, CancellationToken cancellationToken)
            => throw new InvalidOperationException("CreateClusterAsync should not be called during ServiceDatabases.");
    }

    private sealed class RecordingSqlVerifier : IServiceDatabaseSqlVerifier
    {
        public ServiceDatabaseSqlVerificationResult PreLoginResult { get; init; } = ServiceDatabaseSqlVerificationResult.Passed();

        public bool PreLoginVerified { get; private set; }

        public List<string> RevokedServices { get; } = [];

        public bool PostLoginMatrixVerified { get; private set; }

        public Task<ServiceDatabaseSqlVerificationResult> VerifyPreLoginPgHbaEnforcementAsync(
            string writeHost,
            int port,
            CancellationToken cancellationToken)
        {
            if (PreLoginResult.Outcome == ServiceDatabaseSqlVerificationOutcome.Passed)
            {
                PreLoginVerified = true;
            }

            return Task.FromResult(PreLoginResult);
        }

        public Task<ServiceDatabaseSqlVerificationResult> RevokePublicConnectAndVerifyOwnerSessionAsync(
            string writeHost,
            int port,
            ServiceDatabaseTarget target,
            ServiceDatabaseTarget otherTarget,
            string password,
            CancellationToken cancellationToken)
        {
            RevokedServices.Add(target.ServiceKey);
            return Task.FromResult(ServiceDatabaseSqlVerificationResult.Passed());
        }

        public Task<ServiceDatabaseSqlVerificationResult> VerifyPostLoginNegativeMatrixAsync(
            string writeHost,
            int port,
            IReadOnlyDictionary<string, string> servicePasswords,
            CancellationToken cancellationToken)
        {
            PostLoginMatrixVerified = true;
            return Task.FromResult(ServiceDatabaseSqlVerificationResult.Passed());
        }
    }

    private sealed class MutableSecretRecord
    {
        public string Namespace { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Type { get; set; } = "Opaque";
        public int Version { get; set; } = 1;
        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);

        public KubernetesSecretSnapshot ToSnapshot()
            => new(
                Name,
                Namespace,
                $"{Name}-uid",
                Version.ToString(),
                Type,
                null,
                new Dictionary<string, string>(Labels, StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(Data, StringComparer.Ordinal));
    }

    private sealed class InMemoryServiceResourcesClient : ICloudNativePgServiceDatabaseResourcesClient
    {
        public InMemoryServiceResourcesClient(CloudNativePgOptions options)
        {
            Secrets[(options.ServiceDatabases.IdentityDistributionSecret.Namespace, options.ServiceDatabases.IdentityDistributionSecret.Name)] = CreateDistributionSecretRecord(
                options.ServiceDatabases.IdentityDistributionSecret.Namespace,
                options.ServiceDatabases.IdentityDistributionSecret.Name,
                ServiceDatabaseTarget.Identity);
            Secrets[(options.ServiceDatabases.ClinicalDistributionSecret.Namespace, options.ServiceDatabases.ClinicalDistributionSecret.Name)] = CreateDistributionSecretRecord(
                options.ServiceDatabases.ClinicalDistributionSecret.Namespace,
                options.ServiceDatabases.ClinicalDistributionSecret.Name,
                ServiceDatabaseTarget.Clinical);
        }

        private static MutableSecretRecord CreateDistributionSecretRecord(string @namespace, string name, ServiceDatabaseTarget target)
        {
            var record = new MutableSecretRecord
            {
                Namespace = @namespace,
                Name = name,
                Type = ServiceDatabaseTarget.OpaqueSecretType
            };
            record.Labels[CloudNativePgClusterSpec.ManagedByLabelKey] = CloudNativePgClusterSpec.ManagedByLabelValue;
            record.Labels[ServiceDatabaseTarget.PurposeLabelKey] = ServiceDatabaseTarget.DistributionPurposeLabelValue;
            record.Labels[ServiceDatabaseTarget.ServiceLabelKey] = target.ServiceKey;
            return record;
        }

        public Dictionary<(string Ns, string Name), CloudNativePgDatabaseRoleSnapshot> Roles { get; } = new();
        public Dictionary<(string Ns, string Name), CloudNativePgDatabaseSnapshot> Databases { get; } = new();
        public Dictionary<(string Ns, string Name), MutableSecretRecord> Secrets { get; } = new();
        public bool ForceRoleGenerationMismatch { get; init; }
        public bool SimulateNextDistributionConflict { get; set; }
        public int RoleReplaceCount { get; private set; }
        public List<string> CreatedRoleNames { get; } = [];
        public int AuthSecretCreateCount { get; private set; }
        public int SecretReplaceCount { get; private set; }

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> GetDatabaseRoleAsync(
            string @namespace,
            string name,
            CancellationToken cancellationToken)
            => Task.FromResult(Roles.TryGetValue((@namespace, name), out var role)
                ? new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.Found, role)
                : new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.NotFound));

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> CreateDatabaseRoleAsync(
            string @namespace,
            IReadOnlyDictionary<string, object?> manifest,
            CancellationToken cancellationToken)
        {
            var snapshot = ParseRoleFromManifest(manifest, ForceRoleGenerationMismatch ? 0L : 1L);
            CreatedRoleNames.Add(snapshot.Name);
            Roles[(@namespace, snapshot.Name)] = snapshot;
            return Task.FromResult(new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.Created, snapshot));
        }

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>> ReplaceDatabaseRoleAsync(
            string @namespace,
            string name,
            IReadOnlyDictionary<string, object?> manifest,
            CancellationToken cancellationToken)
        {
            RoleReplaceCount++;
            var existing = Roles[(@namespace, name)];
            var nextGen = (existing.Generation ?? 1) + 1;
            var snapshot = ParseRoleFromManifest(manifest, ForceRoleGenerationMismatch ? nextGen - 1 : nextGen, nextGen);
            snapshot = snapshot with { Uid = existing.Uid };
            Roles[(@namespace, name)] = snapshot;
            return Task.FromResult(new KubernetesResourceCallResult<CloudNativePgDatabaseRoleSnapshot>(KubernetesResourceCallStatus.Replaced, snapshot));
        }

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> GetDatabaseAsync(
            string @namespace,
            string name,
            CancellationToken cancellationToken)
            => Task.FromResult(Databases.TryGetValue((@namespace, name), out var db)
                ? new KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>(KubernetesResourceCallStatus.Found, db)
                : new KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>(KubernetesResourceCallStatus.NotFound));

        public Task<KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>> CreateDatabaseAsync(
            string @namespace,
            IReadOnlyDictionary<string, object?> manifest,
            CancellationToken cancellationToken)
        {
            var json = JsonSerializer.SerializeToElement(manifest);
            CloudNativePgDatabaseSnapshot.TryParse(json, out var parsed);
            var snapshot = parsed! with { Uid = $"{parsed.Name}-uid", ResourceVersion = "1", Generation = 1, Applied = true, ObservedGeneration = 1 };
            Databases[(@namespace, snapshot.Name)] = snapshot;
            return Task.FromResult(new KubernetesResourceCallResult<CloudNativePgDatabaseSnapshot>(KubernetesResourceCallStatus.Created, snapshot));
        }

        public Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> GetSecretAsync(
            string @namespace,
            string name,
            CancellationToken cancellationToken)
            => Task.FromResult(Secrets.TryGetValue((@namespace, name), out var secret)
                ? new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.Found, secret.ToSnapshot())
                : new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.NotFound));

        public Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> CreateBasicAuthSecretAsync(
            string @namespace,
            string name,
            IReadOnlyDictionary<string, string> labels,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            var record = new MutableSecretRecord
            {
                Namespace = @namespace,
                Name = name,
                Type = ServiceDatabaseTarget.BasicAuthSecretType
            };
            foreach (var label in labels)
            {
                record.Labels[label.Key] = label.Value;
            }

            record.Data[ServiceDatabaseTarget.AuthSecretUsernameKey] = username;
            record.Data[ServiceDatabaseTarget.AuthSecretPasswordKey] = password;
            Secrets[(@namespace, name)] = record;
            AuthSecretCreateCount++;

            return Task.FromResult(new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.Created, record.ToSnapshot()));
        }

        public Task<KubernetesResourceCallResult<KubernetesSecretSnapshot>> ReplaceSecretDataAsync(
            string @namespace,
            string name,
            KubernetesSecretSnapshot existingSnapshot,
            IReadOnlyDictionary<string, string> updatedData,
            CancellationToken cancellationToken)
        {
            if (SimulateNextDistributionConflict)
            {
                SimulateNextDistributionConflict = false;
                return Task.FromResult(new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.Conflict));
            }

            SecretReplaceCount++;
            var record = Secrets[(@namespace, name)];
            record.Data.Clear();
            foreach (var entry in updatedData)
            {
                record.Data[entry.Key] = entry.Value;
            }

            record.Version++;
            return Task.FromResult(new KubernetesResourceCallResult<KubernetesSecretSnapshot>(KubernetesResourceCallStatus.Replaced, record.ToSnapshot()));
        }

        private static CloudNativePgDatabaseRoleSnapshot ParseRoleFromManifest(
            IReadOnlyDictionary<string, object?> manifest,
            long observedGeneration,
            long generation = 1)
        {
            var json = JsonSerializer.SerializeToElement(manifest);
            CloudNativePgDatabaseRoleSnapshot.TryParse(json, out var parsed);
            return parsed! with
            {
                Uid = $"{parsed.Name}-uid",
                ResourceVersion = generation.ToString(),
                Generation = generation,
                Applied = true,
                ObservedGeneration = observedGeneration
            };
        }
    }
}
