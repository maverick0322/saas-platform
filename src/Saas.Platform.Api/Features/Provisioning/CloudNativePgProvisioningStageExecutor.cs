using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Adaptador real de aprovisionamiento sobre CloudNativePG (PLAT-05A / PLAT-05B).
/// Atiende <c>InfrastructureInstance</c> y, cuando está habilitado en configuración, <c>ServiceDatabases</c>;
/// etapas posteriores (<c>ClinicalPreparation</c> e <c>IdentityPreparation</c>) devuelven <c>AdapterNotConfigured</c>.
/// </summary>
internal sealed class CloudNativePgProvisioningStageExecutor(
    ICloudNativePgClusterClient clusterClient,
    CloudNativePgOptions options,
    TimeProvider timeProvider,
    ILogger<CloudNativePgProvisioningStageExecutor> logger,
    CloudNativePgServiceDatabasesStageHandler? serviceDatabasesHandler = null) : IProvisioningStageExecutor
{
    public bool SupportsRealProvisioning => true;

    public bool SupportsStage(ProvisioningStage stage)
        => stage switch
        {
            ProvisioningStage.InfrastructureInstance => true,
            ProvisioningStage.ServiceDatabases => options.ServiceDatabases.Enabled && serviceDatabasesHandler is not null,
            _ => false
        };

    public void ValidateStartupConfiguration()
    {
        options.Validate();
        clusterClient.EnsureInitialized();
    }

    public async Task<ProvisioningStageResult> ExecuteStageAsync(
        ProvisioningStageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Stage == ProvisioningStage.ServiceDatabases
            && options.ServiceDatabases.Enabled
            && serviceDatabasesHandler is not null)
        {
            return await serviceDatabasesHandler.ExecuteAsync(context, cancellationToken);
        }

        if (context.Stage != ProvisioningStage.InfrastructureInstance)
        {
            return new ProvisioningStageResult(
                ProvisioningStageOutcomeKind.AdapterNotConfigured,
                ErrorCode: "ADAPTER_NOT_CONFIGURED",
                ErrorSummary: $"No external adapter configured for stage '{context.Stage.ToCode()}'.");
        }

        var clusterName = CloudNativePgClusterSpec.BuildClusterName(context.TenantId);
        logger.LogInformation(
            "Executing CloudNativePG stage {Stage} for tenant {TenantId} operation {OperationId} (Cluster={Namespace}/{ClusterName}, VerificationRequired={VerificationRequired}).",
            context.Stage.ToCode(),
            context.TenantId,
            context.OperationId,
            options.Namespace,
            clusterName,
            context.RequiresVerification);

        var initialGet = await clusterClient.GetClusterAsync(options.Namespace, clusterName, cancellationToken);
        switch (initialGet.Status)
        {
            case CloudNativePgGetStatus.Found:
                return await EvaluateAndWaitIfReconcilingAsync(
                    initialGet.Snapshot!,
                    context,
                    clusterName,
                    cancellationToken);

            case CloudNativePgGetStatus.TimeoutUncertain:
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.UncertainTimeout,
                    ErrorCode: initialGet.ErrorCode ?? "CNPG_API_TIMEOUT_UNCERTAIN",
                    ErrorSummary: initialGet.ErrorSummary ?? "Kubernetes API timed out while inspecting existing CloudNativePG cluster.");

            case CloudNativePgGetStatus.TransientError:
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: initialGet.ErrorCode ?? "CNPG_API_UNAVAILABLE",
                    ErrorSummary: initialGet.ErrorSummary ?? "Kubernetes API is temporarily unavailable.");

            case CloudNativePgGetStatus.TerminalError:
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: initialGet.ErrorCode ?? "CNPG_API_REJECTED",
                    ErrorSummary: initialGet.ErrorSummary ?? "Kubernetes API rejected CloudNativePG cluster inspection.");

            case CloudNativePgGetStatus.NotFound:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(initialGet), initialGet.Status, "Unsupported GET status.");
        }

        var manifest = CloudNativePgClusterSpec.BuildClusterManifest(context, options);
        var createResult = await clusterClient.CreateClusterAsync(options.Namespace, manifest, cancellationToken);

        return createResult.Status switch
        {
            CloudNativePgCreateStatus.Created =>
                await EvaluateAndWaitIfReconcilingAsync(
                    createResult.Snapshot!,
                    context,
                    clusterName,
                    cancellationToken),

            CloudNativePgCreateStatus.AlreadyExists =>
                await WaitForReadinessAsync(
                    context,
                    clusterName,
                    expectedUid: null,
                    pollImmediatelyFirst: true,
                    cancellationToken),

            CloudNativePgCreateStatus.TimeoutUncertain =>
                new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.UncertainTimeout,
                    ErrorCode: createResult.ErrorCode ?? "CNPG_CREATE_TIMEOUT_UNCERTAIN",
                    ErrorSummary: createResult.ErrorSummary ?? "Timed out creating CloudNativePG cluster; state verification required."),

            CloudNativePgCreateStatus.TransientError =>
                new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TransientFailure,
                    ErrorCode: createResult.ErrorCode ?? "CNPG_API_UNAVAILABLE",
                    ErrorSummary: createResult.ErrorSummary ?? "Kubernetes API is temporarily unavailable during cluster creation."),

            CloudNativePgCreateStatus.TerminalError =>
                new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: createResult.ErrorCode ?? "CNPG_API_REJECTED",
                    ErrorSummary: createResult.ErrorSummary ?? "Kubernetes API rejected CloudNativePG cluster creation."),

            _ => throw new ArgumentOutOfRangeException(nameof(createResult), createResult.Status, "Unsupported CREATE status.")
        };
    }

    private async Task<ProvisioningStageResult> EvaluateAndWaitIfReconcilingAsync(
        CloudNativePgClusterSnapshot snapshot,
        ProvisioningStageContext context,
        string clusterName,
        CancellationToken cancellationToken)
    {
        var evaluation = CloudNativePgClusterEvaluator.Evaluate(snapshot, context, options);
        switch (evaluation.Kind)
        {
            case CloudNativePgClusterEvaluationKind.Ready:
                logger.LogInformation(
                    "Confirmed ready CloudNativePG cluster {Namespace}/{ClusterName} for tenant {TenantId} (Ref={TechnicalReference}).",
                    options.Namespace,
                    clusterName,
                    context.TenantId,
                    evaluation.TechnicalReference);
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.StageVerified,
                    TechnicalReference: evaluation.TechnicalReference);

            case CloudNativePgClusterEvaluationKind.TerminalConflict:
                logger.LogWarning(
                    "CloudNativePG cluster {Namespace}/{ClusterName} failed terminal validation for tenant {TenantId} with code {ErrorCode}.",
                    options.Namespace,
                    clusterName,
                    context.TenantId,
                    evaluation.ErrorCode);
                return new ProvisioningStageResult(
                    ProvisioningStageOutcomeKind.TerminalFailure,
                    ErrorCode: evaluation.ErrorCode,
                    ErrorSummary: evaluation.ErrorSummary);

            case CloudNativePgClusterEvaluationKind.Reconciling:
                return await WaitForReadinessAsync(
                    context,
                    clusterName,
                    expectedUid: string.IsNullOrWhiteSpace(snapshot.Uid) ? null : snapshot.Uid.Trim(),
                    pollImmediatelyFirst: false,
                    cancellationToken);

            default:
                throw new ArgumentOutOfRangeException(nameof(evaluation), evaluation.Kind, "Unsupported evaluation kind.");
        }
    }

    private async Task<ProvisioningStageResult> WaitForReadinessAsync(
        ProvisioningStageContext context,
        string clusterName,
        string? expectedUid,
        bool pollImmediatelyFirst,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = timeProvider.GetUtcNow().Add(options.ReadinessTimeout);
        var pollImmediately = pollImmediatelyFirst;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!pollImmediately)
            {
                var remaining = deadlineUtc - timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    return CreateWaitTimeoutResult();
                }

                var delay = remaining <= options.ReadinessPollInterval ? remaining : options.ReadinessPollInterval;
                await Task.Delay(delay, timeProvider, cancellationToken);

                // Si solo restaba <= un intervalo o la ventana ya vencio al despertar el timer,
                // no iniciar una nueva consulta GET al borde/fuera de la ventana de disponibilidad.
                if (delay >= remaining || timeProvider.GetUtcNow() >= deadlineUtc)
                {
                    return CreateWaitTimeoutResult();
                }
            }
            else if (timeProvider.GetUtcNow() >= deadlineUtc)
            {
                return CreateWaitTimeoutResult();
            }

            pollImmediately = false;
            var pollResult = await clusterClient.GetClusterAsync(options.Namespace, clusterName, cancellationToken);

            switch (pollResult.Status)
            {
                case CloudNativePgGetStatus.Found:
                {
                    var observedUid = string.IsNullOrWhiteSpace(pollResult.Snapshot!.Uid)
                        ? null
                        : pollResult.Snapshot!.Uid!.Trim();
                    if (expectedUid is not null && !string.Equals(observedUid, expectedUid, StringComparison.Ordinal))
                    {
                        logger.LogWarning(
                            "CloudNativePG cluster {Namespace}/{ClusterName} changed UID during reconciliation for tenant {TenantId}; stopping operation.",
                            options.Namespace,
                            clusterName,
                            context.TenantId);
                        return new ProvisioningStageResult(
                            ProvisioningStageOutcomeKind.TerminalFailure,
                            ErrorCode: "CNPG_CLUSTER_IDENTITY_MISMATCH",
                            ErrorSummary: "CloudNativePG cluster UID changed during reconciliation; operation stopped for administrative review.");
                    }

                    expectedUid ??= observedUid;

                    var evaluation = CloudNativePgClusterEvaluator.Evaluate(pollResult.Snapshot!, context, options);
                    switch (evaluation.Kind)
                    {
                        case CloudNativePgClusterEvaluationKind.Ready:
                            logger.LogInformation(
                                "Confirmed ready CloudNativePG cluster {Namespace}/{ClusterName} for tenant {TenantId} after reconciliation (Ref={TechnicalReference}).",
                                options.Namespace,
                                clusterName,
                                context.TenantId,
                                evaluation.TechnicalReference);
                            return new ProvisioningStageResult(
                                ProvisioningStageOutcomeKind.StageVerified,
                                TechnicalReference: evaluation.TechnicalReference);

                        case CloudNativePgClusterEvaluationKind.TerminalConflict:
                            logger.LogWarning(
                                "CloudNativePG cluster {Namespace}/{ClusterName} transitioned to terminal state {ErrorCode} during reconciliation.",
                                options.Namespace,
                                clusterName,
                                evaluation.ErrorCode);
                            return new ProvisioningStageResult(
                                ProvisioningStageOutcomeKind.TerminalFailure,
                                ErrorCode: evaluation.ErrorCode,
                                ErrorSummary: evaluation.ErrorSummary);

                        case CloudNativePgClusterEvaluationKind.Reconciling:
                            break;
                    }

                    break;
                }

                case CloudNativePgGetStatus.NotFound:
                    return new ProvisioningStageResult(
                        ProvisioningStageOutcomeKind.TransientFailure,
                        ErrorCode: "CNPG_CLUSTER_DISAPPEARED",
                        ErrorSummary: "CloudNativePG cluster was not found while awaiting reconciliation; verification required.");

                case CloudNativePgGetStatus.TerminalError:
                    return new ProvisioningStageResult(
                        ProvisioningStageOutcomeKind.TerminalFailure,
                        ErrorCode: pollResult.ErrorCode ?? "CNPG_API_REJECTED",
                        ErrorSummary: pollResult.ErrorSummary ?? "Kubernetes API rejected CloudNativePG cluster polling.");

                case CloudNativePgGetStatus.TimeoutUncertain:
                case CloudNativePgGetStatus.TransientError:
                    if (timeProvider.GetUtcNow() >= deadlineUtc)
                    {
                        return new ProvisioningStageResult(
                            ProvisioningStageOutcomeKind.TransientFailure,
                            ErrorCode: pollResult.ErrorCode ?? "CNPG_API_UNAVAILABLE",
                            ErrorSummary: pollResult.ErrorSummary ?? "Kubernetes API remained unavailable while awaiting CloudNativePG readiness.");
                    }

                    break;
            }
        }
    }

    private static ProvisioningStageResult CreateWaitTimeoutResult()
        => new(
            ProvisioningStageOutcomeKind.TransientFailure,
            ErrorCode: "CNPG_WAIT",
            ErrorSummary: "CloudNativePG cluster is still reconciling and did not reach Ready state within the cycle wait window.");
}
