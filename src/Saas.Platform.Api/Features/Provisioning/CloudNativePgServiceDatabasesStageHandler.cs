using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Ejecutor de la etapa <c>ServiceDatabases</c> sobre CloudNativePG (PLAT-05B).
/// Verifica la instancia PostgreSQL confirmada, reconcilia <c>DatabaseRole</c>, <c>Database</c>,
/// <c>Secret</c> de autenticación, verifica <c>pg_hba</c> previo a <c>LOGIN</c>, revoca <c>CONNECT</c>
/// de <c>PUBLIC</c> como propietario en TLS, valida aislamiento negativo y distribuye las conexiones.
/// </summary>
internal sealed class CloudNativePgServiceDatabasesStageHandler(
    ICloudNativePgClusterClient clusterClient,
    ICloudNativePgServiceDatabaseResourcesClient resourcesClient,
    IServiceDatabaseSqlVerifier sqlVerifier,
    CloudNativePgOptions options,
    TimeProvider timeProvider,
    ILogger<CloudNativePgServiceDatabasesStageHandler> logger)
{
    public async Task<ProvisioningStageResult> ExecuteAsync(
        ProvisioningStageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CloudNativePgClusterSpec.TryParseTechnicalReference(
                context.InfrastructureResourceRef,
                out var refNamespace,
                out var refClusterName,
                out var refUid))
        {
            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: "CNPG_CLUSTER_IDENTITY_MISMATCH",
                ErrorSummary: "Confirmed InfrastructureInstance technical reference is missing or invalid.");
        }

        var expectedClusterName = CloudNativePgClusterSpec.BuildClusterName(context.TenantId);
        if (!string.Equals(refNamespace, options.Namespace, StringComparison.Ordinal)
            || !string.Equals(refClusterName, expectedClusterName, StringComparison.Ordinal))
        {
            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: "CNPG_CLUSTER_IDENTITY_MISMATCH",
                ErrorSummary: "Confirmed InfrastructureInstance reference does not match configured cluster identity.");
        }

        var clusterResult = await clusterClient.GetClusterAsync(options.Namespace, refClusterName, cancellationToken);
        switch (clusterResult.Status)
        {
            case CloudNativePgGetStatus.NotFound:
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: "CNPG_CLUSTER_MISSING",
                    ErrorSummary: "Expected tenant CloudNativePG cluster was not found.");

            case CloudNativePgGetStatus.TimeoutUncertain:
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.UncertainTimeout,
                    ErrorCode: clusterResult.ErrorCode ?? "CNPG_API_TIMEOUT_UNCERTAIN",
                    ErrorSummary: clusterResult.ErrorSummary ?? "Kubernetes API timed out while verifying tenant cluster.");

            case CloudNativePgGetStatus.TransientError:
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: clusterResult.ErrorCode ?? "CNPG_API_UNAVAILABLE",
                    ErrorSummary: clusterResult.ErrorSummary ?? "Kubernetes API is temporarily unavailable.");

            case CloudNativePgGetStatus.TerminalError:
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: clusterResult.ErrorCode ?? "CNPG_API_REJECTED",
                    ErrorSummary: clusterResult.ErrorSummary ?? "Kubernetes API rejected tenant cluster verification.");

            case CloudNativePgGetStatus.Found:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(clusterResult), clusterResult.Status, "Unsupported GET status.");
        }

        var clusterSnapshot = clusterResult.Snapshot!;
        var clusterEval = CloudNativePgClusterEvaluator.EvaluateForServiceDatabases(
            clusterSnapshot,
            context,
            options,
            refUid);

        if (clusterEval.Kind == CloudNativePgClusterEvaluationKind.TerminalConflict)
        {
            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: clusterEval.ErrorCode ?? "CNPG_CLUSTER_IDENTITY_MISMATCH",
                ErrorSummary: clusterEval.ErrorSummary ?? "Tenant CloudNativePG cluster failed integrity verification.");
        }

        if (clusterEval.Kind == CloudNativePgClusterEvaluationKind.Reconciling)
        {
            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TransientFailure,
                ErrorCode: "CNPG_WAIT",
                ErrorSummary: "Tenant CloudNativePG cluster is currently reconciling and not ready for ServiceDatabases.");
        }

        var writeService = string.IsNullOrWhiteSpace(clusterSnapshot.WriteService)
            ? $"{refClusterName}-rw"
            : clusterSnapshot.WriteService.Trim();
        var writeHost = $"{writeService}.{options.Namespace}.{options.ServiceDatabases.ServiceDnsSuffix}";
        var port = options.ServiceDatabases.PostgreSqlPort;

        logger.LogInformation(
            "Starting ServiceDatabases preparation for tenant {TenantId} operation {OperationId} on cluster {Namespace}/{ClusterName}.",
            context.TenantId,
            context.OperationId,
            options.Namespace,
            refClusterName);

        // Paso 2: Reconciliar roles propietarios (inicialmente NOLOGIN, sin privilegios administrativos).
        var rolesByService = new Dictionary<string, CloudNativePgDatabaseRoleSnapshot>(StringComparer.Ordinal);
        foreach (var target in ServiceDatabaseTarget.MandatoryTargets)
        {
            var (roleSnapshot, failure) = await EnsureRoleReconciledAsync(
                context,
                target,
                refClusterName,
                requireLoginReady: false,
                expectedPasswordSecretName: target.BuildAuthSecretName(context.TenantId),
                cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            rolesByService[target.ServiceKey] = roleSnapshot!;
        }

        // Paso 3: Reconciliar bases privadas y sus propietarios independientes.
        foreach (var target in ServiceDatabaseTarget.MandatoryTargets)
        {
            var dbFailure = await EnsureDatabaseReconciledAsync(context, target, refClusterName, cancellationToken);
            if (dbFailure is not null)
            {
                return dbFailure;
            }
        }

        // Paso 4: Generar o reutilizar Secrets de autenticación CNPG por tenant y servicio (sin persistir ni loguear).
        var passwordsByService = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in ServiceDatabaseTarget.MandatoryTargets)
        {
            var (password, secretFailure) = await EnsureAuthSecretAsync(context, target, cancellationToken);
            if (secretFailure is not null)
            {
                return secretFailure;
            }

            passwordsByService[target.ServiceKey] = password!;
        }

        // Paso 5a: Comprobar en vivo las restricciones de pg_hba antes de habilitar LOGIN.
        var preLoginCheck = await sqlVerifier.VerifyPreLoginPgHbaEnforcementAsync(writeHost, port, cancellationToken);
        if (preLoginCheck.Outcome != ServiceDatabaseSqlVerificationOutcome.Passed)
        {
            return MapSqlFailure(preLoginCheck);
        }

        // Paso 5b: Habilitar LOGIN + passwordSecret tras acreditar pg_hba y esperar reconciliación.
        foreach (var target in ServiceDatabaseTarget.MandatoryTargets)
        {
            var currentRole = rolesByService[target.ServiceKey];
            var authSecretName = target.BuildAuthSecretName(context.TenantId);

            if (currentRole.Login != true || !string.Equals(currentRole.PasswordSecretName, authSecretName, StringComparison.Ordinal))
            {
                var activateFailure = await EnableRoleLoginAndWaitAsync(
                    context,
                    target,
                    refClusterName,
                    authSecretName,
                    currentRole,
                    cancellationToken);
                if (activateFailure is not null)
                {
                    return activateFailure;
                }
            }
        }

        // Pasos 6 y 7: Conectar por TLS como cada propietario, revocar CONNECT de PUBLIC y validar ACL/privilegios.
        var revokeIdentity = await sqlVerifier.RevokePublicConnectAndVerifyOwnerSessionAsync(
            writeHost,
            port,
            ServiceDatabaseTarget.Identity,
            ServiceDatabaseTarget.Clinical,
            passwordsByService[ServiceDatabaseTarget.Identity.ServiceKey],
            cancellationToken);
        if (revokeIdentity.Outcome != ServiceDatabaseSqlVerificationOutcome.Passed)
        {
            return MapSqlFailure(revokeIdentity);
        }

        var revokeClinical = await sqlVerifier.RevokePublicConnectAndVerifyOwnerSessionAsync(
            writeHost,
            port,
            ServiceDatabaseTarget.Clinical,
            ServiceDatabaseTarget.Identity,
            passwordsByService[ServiceDatabaseTarget.Clinical.ServiceKey],
            cancellationToken);
        if (revokeClinical.Outcome != ServiceDatabaseSqlVerificationOutcome.Passed)
        {
            return MapSqlFailure(revokeClinical);
        }

        // Paso 8: Verificar matriz negativa de conectividad post-LOGIN (sin TLS, cruzado, postgres/template1).
        var negativeMatrix = await sqlVerifier.VerifyPostLoginNegativeMatrixAsync(
            writeHost,
            port,
            passwordsByService,
            cancellationToken);
        if (negativeMatrix.Outcome != ServiceDatabaseSqlVerificationOutcome.Passed)
        {
            return MapSqlFailure(negativeMatrix);
        }

        // Paso 9: Distribuir conexiones hacia Secrets consolidados de consumidores solo tras superar todas las verificaciones.
        foreach (var target in ServiceDatabaseTarget.MandatoryTargets)
        {
            var distributeFailure = await DistributeServiceConnectionAsync(
                context,
                target,
                writeHost,
                port,
                passwordsByService[target.ServiceKey],
                cancellationToken);
            if (distributeFailure is not null)
            {
                return distributeFailure;
            }
        }

        logger.LogInformation(
            "Confirmed ServiceDatabases preparation for tenant {TenantId} operation {OperationId}.",
            context.TenantId,
            context.OperationId);

        return new ProvisioningStageResult(ProvisioningStageOutcomeKind.StageVerified);
    }

    private async Task<(CloudNativePgDatabaseRoleSnapshot? Role, ProvisioningStageResult? Failure)> EnsureRoleReconciledAsync(
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        string clusterName,
        bool requireLoginReady,
        string expectedPasswordSecretName,
        CancellationToken cancellationToken)
    {
        var roleResourceName = target.BuildRoleResourceName(context.TenantId);
        var getResult = await resourcesClient.GetDatabaseRoleAsync(options.Namespace, roleResourceName, cancellationToken);

        CloudNativePgDatabaseRoleSnapshot? snapshot;
        if (getResult.Status == KubernetesResourceCallStatus.NotFound)
        {
            var manifest = ServiceDatabaseTarget.BuildDatabaseRoleManifest(
                context,
                options,
                target,
                clusterName,
                loginEnabled: false);

            var createResult = await resourcesClient.CreateDatabaseRoleAsync(options.Namespace, manifest, cancellationToken);
            if (createResult.Status == KubernetesResourceCallStatus.Conflict)
            {
                var afterConflict = await resourcesClient.GetDatabaseRoleAsync(options.Namespace, roleResourceName, cancellationToken);
                if (afterConflict.Status != KubernetesResourceCallStatus.Found)
                {
                    return (null, MapKubernetesFailure(afterConflict, "CNPG_API_UNAVAILABLE"));
                }

                snapshot = afterConflict.Snapshot!;
            }
            else if (createResult.Status == KubernetesResourceCallStatus.Created)
            {
                snapshot = createResult.Snapshot!;
            }
            else
            {
                return (null, MapKubernetesFailure(createResult, "CNPG_API_UNAVAILABLE"));
            }
        }
        else if (getResult.Status == KubernetesResourceCallStatus.Found)
        {
            snapshot = getResult.Snapshot!;
        }
        else
        {
            return (null, MapKubernetesFailure(getResult, "CNPG_API_UNAVAILABLE"));
        }

        return await WaitForRoleReadyAsync(
            snapshot,
            context,
            target,
            clusterName,
            requireLoginReady,
            expectedPasswordSecretName,
            cancellationToken);
    }

    private async Task<(CloudNativePgDatabaseRoleSnapshot? Role, ProvisioningStageResult? Failure)> WaitForRoleReadyAsync(
        CloudNativePgDatabaseRoleSnapshot initialSnapshot,
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        string clusterName,
        bool requireLoginReady,
        string expectedPasswordSecretName,
        CancellationToken cancellationToken)
    {
        var initialEval = initialSnapshot.Evaluate(context, options, target, clusterName, requireLoginReady, expectedPasswordSecretName);
        if (initialEval.Kind == ServiceResourceEvaluationKind.Ready)
        {
            return (initialSnapshot, null);
        }

        if (initialEval.Kind == ServiceResourceEvaluationKind.TerminalConflict)
        {
            return (null, new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: initialEval.ErrorCode,
                ErrorSummary: initialEval.ErrorSummary));
        }

        var roleResourceName = target.BuildRoleResourceName(context.TenantId);
        var deadlineUtc = timeProvider.GetUtcNow().Add(options.ReadinessTimeout);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadlineUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return (null, new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: "CNPG_ROLE_WAIT",
                    ErrorSummary: $"DatabaseRole for service '{target.ServiceKey}' did not finish reconciling within the cycle wait window."));
            }

            var delay = remaining <= options.ReadinessPollInterval ? remaining : options.ReadinessPollInterval;
            await Task.Delay(delay, timeProvider, cancellationToken);

            if (delay >= remaining || timeProvider.GetUtcNow() >= deadlineUtc)
            {
                return (null, new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: "CNPG_ROLE_WAIT",
                    ErrorSummary: $"DatabaseRole for service '{target.ServiceKey}' did not finish reconciling within the cycle wait window."));
            }

            var pollResult = await resourcesClient.GetDatabaseRoleAsync(options.Namespace, roleResourceName, cancellationToken);
            if (pollResult.Status != KubernetesResourceCallStatus.Found)
            {
                if (pollResult.Status == KubernetesResourceCallStatus.TerminalError)
                {
                    return (null, MapKubernetesFailure(pollResult, "CNPG_API_REJECTED"));
                }

                if (timeProvider.GetUtcNow() >= deadlineUtc)
                {
                    return (null, MapKubernetesFailure(pollResult, "CNPG_ROLE_WAIT"));
                }

                continue;
            }

            var eval = pollResult.Snapshot!.Evaluate(context, options, target, clusterName, requireLoginReady, expectedPasswordSecretName);
            if (eval.Kind == ServiceResourceEvaluationKind.Ready)
            {
                return (pollResult.Snapshot, null);
            }

            if (eval.Kind == ServiceResourceEvaluationKind.TerminalConflict)
            {
                return (null, new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: eval.ErrorCode,
                    ErrorSummary: eval.ErrorSummary));
            }
        }
    }

    private async Task<ProvisioningStageResult?> EnsureDatabaseReconciledAsync(
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        string clusterName,
        CancellationToken cancellationToken)
    {
        var dbResourceName = target.BuildDatabaseResourceName(context.TenantId);
        var getResult = await resourcesClient.GetDatabaseAsync(options.Namespace, dbResourceName, cancellationToken);

        CloudNativePgDatabaseSnapshot? snapshot;
        if (getResult.Status == KubernetesResourceCallStatus.NotFound)
        {
            var manifest = ServiceDatabaseTarget.BuildDatabaseManifest(context, options, target, clusterName);
            var createResult = await resourcesClient.CreateDatabaseAsync(options.Namespace, manifest, cancellationToken);

            if (createResult.Status == KubernetesResourceCallStatus.Conflict)
            {
                var afterConflict = await resourcesClient.GetDatabaseAsync(options.Namespace, dbResourceName, cancellationToken);
                if (afterConflict.Status != KubernetesResourceCallStatus.Found)
                {
                    return MapKubernetesFailure(afterConflict, "CNPG_API_UNAVAILABLE");
                }

                snapshot = afterConflict.Snapshot!;
            }
            else if (createResult.Status == KubernetesResourceCallStatus.Created)
            {
                snapshot = createResult.Snapshot!;
            }
            else
            {
                return MapKubernetesFailure(createResult, "CNPG_API_UNAVAILABLE");
            }
        }
        else if (getResult.Status == KubernetesResourceCallStatus.Found)
        {
            snapshot = getResult.Snapshot!;
        }
        else
        {
            return MapKubernetesFailure(getResult, "CNPG_API_UNAVAILABLE");
        }

        var initialEval = snapshot.Evaluate(context, options, target, clusterName);
        if (initialEval.Kind == ServiceResourceEvaluationKind.Ready)
        {
            return null;
        }

        if (initialEval.Kind == ServiceResourceEvaluationKind.TerminalConflict)
        {
            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: initialEval.ErrorCode,
                ErrorSummary: initialEval.ErrorSummary);
        }

        var deadlineUtc = timeProvider.GetUtcNow().Add(options.ReadinessTimeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadlineUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: "CNPG_DATABASE_WAIT",
                    ErrorSummary: $"Database for service '{target.ServiceKey}' did not finish reconciling within the cycle wait window.");
            }

            var delay = remaining <= options.ReadinessPollInterval ? remaining : options.ReadinessPollInterval;
            await Task.Delay(delay, timeProvider, cancellationToken);

            if (delay >= remaining || timeProvider.GetUtcNow() >= deadlineUtc)
            {
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: "CNPG_DATABASE_WAIT",
                    ErrorSummary: $"Database for service '{target.ServiceKey}' did not finish reconciling within the cycle wait window.");
            }

            var pollResult = await resourcesClient.GetDatabaseAsync(options.Namespace, dbResourceName, cancellationToken);
            if (pollResult.Status != KubernetesResourceCallStatus.Found)
            {
                if (pollResult.Status == KubernetesResourceCallStatus.TerminalError)
                {
                    return MapKubernetesFailure(pollResult, "CNPG_API_REJECTED");
                }

                if (timeProvider.GetUtcNow() >= deadlineUtc)
                {
                    return MapKubernetesFailure(pollResult, "CNPG_DATABASE_WAIT");
                }

                continue;
            }

            var eval = pollResult.Snapshot!.Evaluate(context, options, target, clusterName);
            if (eval.Kind == ServiceResourceEvaluationKind.Ready)
            {
                return null;
            }

            if (eval.Kind == ServiceResourceEvaluationKind.TerminalConflict)
            {
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: eval.ErrorCode,
                    ErrorSummary: eval.ErrorSummary);
            }
        }
    }

    private async Task<(string? Password, ProvisioningStageResult? Failure)> EnsureAuthSecretAsync(
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        CancellationToken cancellationToken)
    {
        var secretName = target.BuildAuthSecretName(context.TenantId);
        var getResult = await resourcesClient.GetSecretAsync(options.Namespace, secretName, cancellationToken);

        if (getResult.Status == KubernetesResourceCallStatus.Found)
        {
            return getResult.Snapshot!.TryValidateAuthSecret(context, options, target, out var existingPassword)
                ? (existingPassword, null)
                : (null, new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: "CNPG_AUTH_SECRET_CONFLICT",
                    ErrorSummary: $"Existing authentication Secret for service '{target.ServiceKey}' is incompatible and cannot be reused."));
        }

        if (getResult.Status != KubernetesResourceCallStatus.NotFound)
        {
            return (null, MapKubernetesFailure(getResult, "CNPG_API_UNAVAILABLE"));
        }

        var generatedPassword = ServiceDatabaseTarget.GenerateSecurePassword();
        var labels = ServiceDatabaseTarget.BuildResourceLabels(context, target, includeCnpgReload: true);

        var createResult = await resourcesClient.CreateBasicAuthSecretAsync(
            options.Namespace,
            secretName,
            labels,
            target.RoleName,
            generatedPassword,
            cancellationToken);

        if (createResult.Status == KubernetesResourceCallStatus.Conflict)
        {
            var afterConflict = await resourcesClient.GetSecretAsync(options.Namespace, secretName, cancellationToken);
            if (afterConflict.Status != KubernetesResourceCallStatus.Found)
            {
                return (null, MapKubernetesFailure(afterConflict, "CNPG_API_UNAVAILABLE"));
            }

            return afterConflict.Snapshot!.TryValidateAuthSecret(context, options, target, out var conflictPassword)
                ? (conflictPassword, null)
                : (null, new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: "CNPG_AUTH_SECRET_CONFLICT",
                    ErrorSummary: $"Authentication Secret created concurrently for service '{target.ServiceKey}' is incompatible."));
        }

        if (createResult.Status != KubernetesResourceCallStatus.Created)
        {
            return (null, MapKubernetesFailure(createResult, "CNPG_API_UNAVAILABLE"));
        }

        return (generatedPassword, null);
    }

    private async Task<ProvisioningStageResult?> EnableRoleLoginAndWaitAsync(
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        string clusterName,
        string authSecretName,
        CloudNativePgDatabaseRoleSnapshot currentSnapshot,
        CancellationToken cancellationToken)
    {
        var roleResourceName = target.BuildRoleResourceName(context.TenantId);
        var snapshotToActivate = currentSnapshot;
        CloudNativePgDatabaseRoleSnapshot? replacedSnapshot = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var evalBeforeUpdate = snapshotToActivate.Evaluate(context, options, target, clusterName, requireLoginReady: false, authSecretName);
            if (evalBeforeUpdate.Kind == ServiceResourceEvaluationKind.TerminalConflict)
            {
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: evalBeforeUpdate.ErrorCode,
                    ErrorSummary: evalBeforeUpdate.ErrorSummary);
            }

            if (snapshotToActivate.Login == true
                && string.Equals(snapshotToActivate.PasswordSecretName, authSecretName, StringComparison.Ordinal))
            {
                replacedSnapshot = snapshotToActivate;
                break;
            }

            var updatedManifest = ServiceDatabaseTarget.BuildDatabaseRoleManifest(
                context,
                options,
                target,
                clusterName,
                loginEnabled: true,
                passwordSecretName: authSecretName,
                resourceVersion: snapshotToActivate.ResourceVersion);

            var replaceResult = await resourcesClient.ReplaceDatabaseRoleAsync(
                options.Namespace,
                roleResourceName,
                updatedManifest,
                cancellationToken);

            if (replaceResult.Status == KubernetesResourceCallStatus.Replaced)
            {
                replacedSnapshot = replaceResult.Snapshot!;
                break;
            }

            if (replaceResult.Status != KubernetesResourceCallStatus.Conflict)
            {
                return MapKubernetesFailure(replaceResult, "CNPG_API_UNAVAILABLE");
            }

            var reloadResult = await resourcesClient.GetDatabaseRoleAsync(options.Namespace, roleResourceName, cancellationToken);
            if (reloadResult.Status != KubernetesResourceCallStatus.Found)
            {
                return MapKubernetesFailure(reloadResult, "CNPG_API_UNAVAILABLE");
            }

            snapshotToActivate = reloadResult.Snapshot!;
        }

        if (replacedSnapshot is null)
        {
            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TransientFailure,
                ErrorCode: "CNPG_ROLE_WAIT",
                ErrorSummary: $"DatabaseRole activation for service '{target.ServiceKey}' encountered concurrent modifications; retry scheduled.");
        }

        var (_, failure) = await WaitForRoleReadyAsync(
            replacedSnapshot,
            context,
            target,
            clusterName,
            requireLoginReady: true,
            expectedPasswordSecretName: authSecretName,
            cancellationToken);

        return failure;
    }

    private async Task<ProvisioningStageResult?> DistributeServiceConnectionAsync(
        ProvisioningStageContext context,
        ServiceDatabaseTarget target,
        string writeHost,
        int port,
        string password,
        CancellationToken cancellationToken)
    {
        var targetSecret = ServiceDatabaseTarget.ResolveDistributionSecret(target, options.ServiceDatabases);
        var entryKey = ServiceDatabaseTarget.BuildDistributionEntryKey(context.TenantId);
        var expectedConnectionString = ServiceDatabaseTarget.BuildServiceConnectionString(writeHost, port, target, password);

        for (var attempt = 0; attempt < options.ServiceDatabases.SecretDistributionMaxConcurrencyRetries; attempt++)
        {
            var getResult = await resourcesClient.GetSecretAsync(targetSecret.Namespace, targetSecret.Name, cancellationToken);
            if (getResult.Status == KubernetesResourceCallStatus.NotFound)
            {
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: "CNPG_DISTRIBUTION_SECRET_MISSING",
                    ErrorSummary: $"Pre-existing distribution Secret for service '{target.ServiceKey}' was not found.");
            }

            if (getResult.Status != KubernetesResourceCallStatus.Found)
            {
                return MapKubernetesFailure(getResult, "CNPG_API_UNAVAILABLE");
            }

            var existingSecret = getResult.Snapshot!;
            if (!string.IsNullOrWhiteSpace(existingSecret.DeletionTimestamp))
            {
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: "CNPG_DISTRIBUTION_SECRET_CONFLICT",
                    ErrorSummary: $"Distribution Secret for service '{target.ServiceKey}' is marked for deletion.");
            }

            if (existingSecret.Data.TryGetValue(entryKey, out var existingEntry))
            {
                if (ServiceDatabaseTarget.AreConnectionStringsEquivalent(existingEntry, expectedConnectionString))
                {
                    return null;
                }

                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: "CNPG_DISTRIBUTION_SECRET_CONFLICT",
                    ErrorSummary: $"Distribution Secret for service '{target.ServiceKey}' already contains an incompatible tenant connection.");
            }

            var updatedData = new Dictionary<string, string>(existingSecret.Data, StringComparer.Ordinal)
            {
                [entryKey] = expectedConnectionString
            };

            var replaceResult = await resourcesClient.ReplaceSecretDataAsync(
                targetSecret.Namespace,
                targetSecret.Name,
                existingSecret,
                updatedData,
                cancellationToken);

            if (replaceResult.Status == KubernetesResourceCallStatus.Replaced)
            {
                return null;
            }

            if (replaceResult.Status == KubernetesResourceCallStatus.Conflict)
            {
                continue;
            }

            return MapKubernetesFailure(replaceResult, "CNPG_API_UNAVAILABLE");
        }

        return new ProvisioningStageResult(
            ProvisioningStageOutcomeKind.TransientFailure,
            ErrorCode: "CNPG_DISTRIBUTION_CONCURRENCY_EXHAUSTED",
            ErrorSummary: $"Concurrent updates exhausted retries while distributing connection for service '{target.ServiceKey}'.");
    }

    private static ProvisioningStageResult MapKubernetesFailure<TSnapshot>(
        KubernetesResourceCallResult<TSnapshot> result,
        string fallbackCode)
        => result.Status switch
        {
            KubernetesResourceCallStatus.TimeoutUncertain => new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.UncertainTimeout,
                ErrorCode: result.ErrorCode ?? fallbackCode,
                ErrorSummary: result.ErrorSummary ?? "Kubernetes API operation timed out; verification required."),
            KubernetesResourceCallStatus.TerminalError => new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: result.ErrorCode ?? "CNPG_API_REJECTED",
                ErrorSummary: result.ErrorSummary ?? "Kubernetes API rejected service database provisioning."),
            _ => new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TransientFailure,
                ErrorCode: result.ErrorCode ?? fallbackCode,
                ErrorSummary: result.ErrorSummary ?? "Kubernetes API is temporarily unavailable.")
        };

    private static ProvisioningStageResult MapSqlFailure(ServiceDatabaseSqlVerificationResult sqlResult)
        => sqlResult.Outcome switch
        {
            ServiceDatabaseSqlVerificationOutcome.TerminalFailure => new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TerminalFailure,
                ErrorCode: sqlResult.ErrorCode ?? "PG_ACL_VERIFICATION_FAILED",
                ErrorSummary: sqlResult.ErrorSummary ?? "PostgreSQL service isolation verification failed."),
            _ => new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.TransientFailure,
                ErrorCode: sqlResult.ErrorCode ?? "PG_INFRASTRUCTURE_UNREACHABLE",
                ErrorSummary: sqlResult.ErrorSummary ?? "PostgreSQL instance was unavailable or rules not yet active.")
        };
}
