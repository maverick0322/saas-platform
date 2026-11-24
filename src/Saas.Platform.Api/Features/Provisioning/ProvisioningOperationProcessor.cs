using Saas.Platform.Api.Domain;

namespace Saas.Platform.Api.Features.Provisioning;

public enum ProvisioningCycleOutcome
{
    Idle = 0,
    DeferredRetry = 1,
    RetriesExhausted = 2,
    TerminalFailed = 3,
    LeaseLost = 4,
    SimulatedRejected = 5,
    AdapterNotConfigured = 6,
    AwaitingRealActivation = 7
}

public sealed record ProvisioningCycleResult(
    ProvisioningCycleOutcome Outcome,
    Guid? OperationId = null,
    long? LeaseVersion = null,
    int? AttemptCount = null,
    ProvisioningStage? LastStage = null);

/// <summary>
/// Procesador por ciclo de una operación de aprovisionamiento con arrendamiento cercado (fencing),
/// progreso por etapa y barrera obligatoria contra activaciones simuladas o falsas (PLAT-02C).
/// </summary>
public sealed class ProvisioningOperationProcessor(
    ProvisioningLeaseStore leaseStore,
    IProvisioningStageExecutor stageExecutor,
    TimeProvider timeProvider,
    ProvisioningWorkerOptions options,
    ILogger<ProvisioningOperationProcessor> logger)
{
    public async Task<ProvisioningCycleResult> ProcessNextAsync(
        string workerId,
        CancellationToken cancellationToken)
    {
        options.Validate();

        var utcNow = timeProvider.GetUtcNow().ToUniversalTime();
        var expiredCount = await leaseStore.MarkExpiredExhaustedLeasesAsync(
            utcNow,
            options.MaxTotalAttempts,
            cancellationToken);

        if (expiredCount > 0)
        {
            logger.LogWarning(
                "Marked {ExpiredCount} expired provisioning operation(s) at attempt cap for manual intervention.",
                expiredCount);
        }

        var acquired = await leaseStore.TryAcquireNextAsync(
            workerId,
            utcNow,
            options.LeaseDuration,
            options.MaxTotalAttempts,
            cancellationToken);

        if (acquired is null)
        {
            return new ProvisioningCycleResult(ProvisioningCycleOutcome.Idle);
        }

        var current = acquired;
        logger.LogInformation(
            "Acquired provisioning operation {OperationId} for tenant {TenantId} ({ProvisioningRequestId}) attempt {AttemptCount} with lease version {LeaseVersion}.",
            current.Token.OperationId,
            current.TenantId,
            current.ProvisioningRequestId,
            current.AttemptCount,
            current.Token.LeaseVersion);

        foreach (var stage in ProvisioningStageExtensions.OrderedStages)
        {
            var stageState = current.GetStageState(stage);
            if (stageState == ProvisioningStageState.Confirmed)
            {
                continue;
            }

            var context = new ProvisioningStageContext(
                OperationId: current.Token.OperationId,
                TenantId: current.TenantId,
                TenantPublicId: current.TenantPublicId,
                TenantName: current.TenantName,
                ProvisioningRequestId: current.ProvisioningRequestId,
                InitialAdminFullName: current.InitialAdminFullName,
                InitialAdminEmail: current.InitialAdminEmail,
                Stage: stage,
                RequiresVerification: stageState == ProvisioningStageState.VerificationRequired,
                AttemptCount: current.AttemptCount,
                InfrastructureResourceRef: current.InfrastructureResourceRef,
                ClinicalMigrationVersion: current.ClinicalMigrationVersion,
                InitialAdminAccountId: current.InitialAdminAccountId);

            using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var renewalStopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var leaseLostFlag = 0;
            var lastKnownLeaseUntilTicks = current.LeaseUntilUtc.UtcTicks;

            var renewalTask = RunLeaseRenewalLoopAsync(
                current.Token,
                options.LeaseDuration,
                options.LeaseRenewalInterval,
                stageCts,
                () => Volatile.Write(ref leaseLostFlag, 1),
                renewedUntilUtc => Interlocked.Exchange(ref lastKnownLeaseUntilTicks, renewedUntilUtc.UtcTicks),
                () => Interlocked.Read(ref lastKnownLeaseUntilTicks),
                renewalStopCts.Token);

            ProvisioningStageResult stageResult;
            try
            {
                stageResult = await stageExecutor.ExecuteStageAsync(context, stageCts.Token);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref leaseLostFlag) == 1 && !cancellationToken.IsCancellationRequested)
            {
                return LogAndReturnLeaseLost(current, stage);
            }
            finally
            {
                await renewalStopCts.CancelAsync();
                try
                {
                    await renewalTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (Volatile.Read(ref leaseLostFlag) == 1)
            {
                return LogAndReturnLeaseLost(current, stage);
            }

            current = current with
            {
                LeaseUntilUtc = new DateTimeOffset(Interlocked.Read(ref lastKnownLeaseUntilTicks), TimeSpan.Zero)
            };

            var afterStageUtc = timeProvider.GetUtcNow().ToUniversalTime();

            switch (stageResult.Outcome)
            {
                case ProvisioningStageOutcomeKind.StageVerified:
                {
                    var recorded = await leaseStore.TryRecordStageConfirmedAsync(
                        current.Token,
                        stage,
                        stageResult.TechnicalReference,
                        afterStageUtc,
                        cancellationToken);

                    if (!recorded)
                    {
                        return LogAndReturnLeaseLost(current, stage);
                    }

                    current = current.WithStageConfirmed(stage, stageResult.TechnicalReference);
                    break;
                }

                case ProvisioningStageOutcomeKind.SimulatedOnly:
                    return await ReleaseRetryableFailureAsync(
                        current,
                        markStageVerificationRequired: stage,
                        errorCode: stageResult.ErrorCode ?? "SIMULATED_EVIDENCE_REJECTED",
                        errorSummary: stageResult.ErrorSummary
                            ?? $"Simulated response for stage '{stage.ToCode()}' cannot be recorded as confirmed evidence.",
                        utcNow: afterStageUtc,
                        forceManualIntervention: true,
                        outcomeWhenReleased: ProvisioningCycleOutcome.SimulatedRejected,
                        lastStage: stage,
                        cancellationToken: cancellationToken);

                case ProvisioningStageOutcomeKind.AdapterNotConfigured:
                    return await ReleaseRetryableFailureAsync(
                        current,
                        markStageVerificationRequired: null,
                        errorCode: stageResult.ErrorCode ?? "ADAPTER_NOT_CONFIGURED",
                        errorSummary: stageResult.ErrorSummary
                            ?? $"No external adapter configured for stage '{stage.ToCode()}'.",
                        utcNow: afterStageUtc,
                        forceManualIntervention: true,
                        outcomeWhenReleased: ProvisioningCycleOutcome.AdapterNotConfigured,
                        lastStage: stage,
                        cancellationToken: cancellationToken);

                case ProvisioningStageOutcomeKind.UncertainTimeout:
                    return await ReleaseRetryableFailureAsync(
                        current,
                        markStageVerificationRequired: stage,
                        errorCode: stageResult.ErrorCode ?? "STAGE_TIMEOUT_UNCERTAIN",
                        errorSummary: stageResult.ErrorSummary
                            ?? $"External timeout during stage '{stage.ToCode()}'; verification required before proceeding.",
                        utcNow: afterStageUtc,
                        forceManualIntervention: false,
                        outcomeWhenReleased: ProvisioningCycleOutcome.DeferredRetry,
                        lastStage: stage,
                        cancellationToken: cancellationToken);

                case ProvisioningStageOutcomeKind.TransientFailure:
                    return await ReleaseRetryableFailureAsync(
                        current,
                        markStageVerificationRequired: null,
                        errorCode: stageResult.ErrorCode ?? "TRANSIENT_STAGE_FAILURE",
                        errorSummary: stageResult.ErrorSummary
                            ?? $"Transient error executing stage '{stage.ToCode()}'.",
                        utcNow: afterStageUtc,
                        forceManualIntervention: false,
                        outcomeWhenReleased: ProvisioningCycleOutcome.DeferredRetry,
                        lastStage: stage,
                        cancellationToken: cancellationToken);

                case ProvisioningStageOutcomeKind.TerminalFailure:
                {
                    var released = await leaseStore.TryReleaseWithTerminalFailureAsync(
                        current.Token,
                        stageResult.ErrorCode ?? "TERMINAL_STAGE_FAILURE",
                        stageResult.ErrorSummary
                            ?? $"Non-recoverable error executing stage '{stage.ToCode()}'.",
                        afterStageUtc,
                        cancellationToken);

                    if (!released)
                    {
                        return LogAndReturnLeaseLost(current, stage);
                    }

                    logger.LogError(
                        "Provisioning operation {OperationId} transitioned to FAILED_TERMINAL at stage {Stage} on attempt {AttemptCount}.",
                        current.Token.OperationId,
                        stage.ToCode(),
                        current.AttemptCount);

                    return new ProvisioningCycleResult(
                        ProvisioningCycleOutcome.TerminalFailed,
                        current.Token.OperationId,
                        current.Token.LeaseVersion,
                        current.AttemptCount,
                        stage);
                }

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(stageResult),
                        stageResult.Outcome,
                        "Unsupported stage outcome kind.");
            }
        }

        // Barrera de seguridad PLAT-02C: la activación efectiva (COMPLETED + ACTIVA) no está habilitada
        // mientras no existan adaptadores reales ratificados y flujo de activación autorizado.
        var completionCheckUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var parked = await leaseStore.TryReleaseWithRecoverableFailureAsync(
            current.Token,
            markStageVerificationRequired: null,
            errorCode: "REAL_ACTIVATION_NOT_ENABLED",
            errorSummary: stageExecutor.SupportsRealProvisioning
                ? "Effective tenant activation is deferred beyond PLAT-02C."
                : "All stages evaluated without real external adapters; tenant remains PENDIENTE_ADMIN.",
            utcNow: completionCheckUtc,
            nextAttemptAtUtc: null,
            manualInterventionRequired: true,
            cancellationToken: cancellationToken);

        if (!parked)
        {
            return LogAndReturnLeaseLost(current, ProvisioningStage.IdentityPreparation);
        }

        logger.LogInformation(
            "Provisioning operation {OperationId} parked safely at AwaitingRealActivation; tenant {TenantId} remains PENDIENTE_ADMIN.",
            current.Token.OperationId,
            current.TenantId);

        return new ProvisioningCycleResult(
            ProvisioningCycleOutcome.AwaitingRealActivation,
            current.Token.OperationId,
            current.Token.LeaseVersion,
            current.AttemptCount,
            ProvisioningStage.IdentityPreparation);
    }

    private async Task<ProvisioningCycleResult> ReleaseRetryableFailureAsync(
        AcquiredProvisioningOperation current,
        ProvisioningStage? markStageVerificationRequired,
        string errorCode,
        string errorSummary,
        DateTimeOffset utcNow,
        bool forceManualIntervention,
        ProvisioningCycleOutcome outcomeWhenReleased,
        ProvisioningStage lastStage,
        CancellationToken cancellationToken)
    {
        var retriesExhausted = current.AttemptCount >= options.MaxTotalAttempts;
        var manualIntervention = forceManualIntervention || retriesExhausted;
        DateTimeOffset? nextAttemptAtUtc = manualIntervention
            ? null
            : utcNow.Add(options.CalculateRetryBackoff(current.AttemptCount));

        var released = await leaseStore.TryReleaseWithRecoverableFailureAsync(
            current.Token,
            markStageVerificationRequired,
            errorCode,
            errorSummary,
            utcNow,
            nextAttemptAtUtc,
            manualIntervention,
            cancellationToken);

        if (!released)
        {
            return LogAndReturnLeaseLost(current, lastStage);
        }

        var finalOutcome = !forceManualIntervention && retriesExhausted
            ? ProvisioningCycleOutcome.RetriesExhausted
            : outcomeWhenReleased;

        logger.LogWarning(
            "Provisioning operation {OperationId} released at stage {Stage} with outcome {Outcome} (attempt {AttemptCount}/{MaxTotalAttempts}, manualIntervention={ManualIntervention}).",
            current.Token.OperationId,
            lastStage.ToCode(),
            finalOutcome,
            current.AttemptCount,
            options.MaxTotalAttempts,
            manualIntervention);

        return new ProvisioningCycleResult(
            finalOutcome,
            current.Token.OperationId,
            current.Token.LeaseVersion,
            current.AttemptCount,
            lastStage);
    }

    private ProvisioningCycleResult LogAndReturnLeaseLost(
        AcquiredProvisioningOperation current,
        ProvisioningStage stage)
    {
        logger.LogWarning(
            "Provisioning worker lost lease fencing for operation {OperationId} (leaseVersion={LeaseVersion}) at stage {Stage}.",
            current.Token.OperationId,
            current.Token.LeaseVersion,
            stage.ToCode());

        return new ProvisioningCycleResult(
            ProvisioningCycleOutcome.LeaseLost,
            current.Token.OperationId,
            current.Token.LeaseVersion,
            current.AttemptCount,
            stage);
    }

    private async Task RunLeaseRenewalLoopAsync(
        ProvisioningLeaseToken token,
        TimeSpan leaseDuration,
        TimeSpan renewalInterval,
        CancellationTokenSource stageCts,
        Action markLeaseLost,
        Action<DateTimeOffset> updateLeaseUntil,
        Func<long> getLastKnownLeaseUntilTicks,
        CancellationToken renewalStopToken)
    {
        using var timer = new PeriodicTimer(renewalInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(renewalStopToken))
        {
            var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
            try
            {
                var renewed = await leaseStore.TryRenewLeaseAsync(
                    token,
                    nowUtc,
                    leaseDuration,
                    renewalStopToken);

                if (renewed)
                {
                    updateLeaseUntil(nowUtc.Add(leaseDuration));
                    continue;
                }

                markLeaseLost();
                await stageCts.CancelAsync();
                return;
            }
            catch (OperationCanceledException) when (renewalStopToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Transient error renewing lease for operation {OperationId} (leaseVersion={LeaseVersion}).",
                    token.OperationId,
                    token.LeaseVersion);

                if (timeProvider.GetUtcNow().ToUniversalTime().UtcTicks >= getLastKnownLeaseUntilTicks())
                {
                    markLeaseLost();
                    await stageCts.CancelAsync();
                    return;
                }
            }
        }
    }
}
