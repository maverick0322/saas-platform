using System.Text.RegularExpressions;

namespace Saas.Platform.Api.Domain;

/// <summary>
/// Intención durable y reintentable de aprovisionamiento inicial para una clínica en la base global de Platform.
/// Conserva su identificador de solicitud (<see cref="ProvisioningRequestId"/>), los datos del administrador inicial,
/// el arrendamiento (lease + fencing) y el progreso por etapa entre reintentos.
/// </summary>
public sealed partial class TenantProvisioningOperation
{
    public const int ProvisioningRequestIdMaxLength = 64;
    public const int InitialAdminFullNameMinLength = 2;
    public const int InitialAdminFullNameMaxLength = 150;
    public const int InitialAdminEmailMaxLength = 254;
    public const int StatusMaxLength = 32;
    public const int LastErrorCodeMinLength = 2;
    public const int LastErrorCodeMaxLength = 64;
    public const int LastErrorSummaryMaxLength = 500;
    public const int LeaseOwnerMaxLength = 128;
    public const int StageStateMaxLength = 32;
    public const int TechnicalReferenceMaxLength = 128;

    private TenantProvisioningOperation()
    {
    }

    private TenantProvisioningOperation(
        Guid id,
        Guid tenantId,
        string provisioningRequestId,
        string initialAdminFullName,
        string initialAdminEmail,
        DateTimeOffset createdAtUtc,
        Guid concurrencyStamp)
    {
        Id = id;
        TenantId = tenantId;
        ProvisioningRequestId = provisioningRequestId;
        InitialAdminFullName = initialAdminFullName;
        InitialAdminEmail = initialAdminEmail;
        Status = ProvisioningOperationStatus.Pending;
        AttemptCount = 0;
        LeaseVersion = 0;
        ManualInterventionRequired = false;
        InfrastructureStageState = ProvisioningStageState.Pending;
        ServiceDatabasesStageState = ProvisioningStageState.Pending;
        ClinicalStageState = ProvisioningStageState.Pending;
        IdentityStageState = ProvisioningStageState.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        ConcurrencyStamp = concurrencyStamp;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ProvisioningRequestId { get; private set; } = string.Empty;

    public string InitialAdminFullName { get; private set; } = string.Empty;

    public string InitialAdminEmail { get; private set; } = string.Empty;

    public ProvisioningOperationStatus Status { get; private set; } = ProvisioningOperationStatus.Pending;

    public int AttemptCount { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorSummary { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseUntilUtc { get; private set; }

    public long LeaseVersion { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public bool ManualInterventionRequired { get; private set; }

    public ProvisioningStageState InfrastructureStageState { get; private set; } = ProvisioningStageState.Pending;

    public ProvisioningStageState ServiceDatabasesStageState { get; private set; } = ProvisioningStageState.Pending;

    public ProvisioningStageState ClinicalStageState { get; private set; } = ProvisioningStageState.Pending;

    public ProvisioningStageState IdentityStageState { get; private set; } = ProvisioningStageState.Pending;

    public string? InfrastructureResourceRef { get; private set; }

    public string? ClinicalMigrationVersion { get; private set; }

    public string? InitialAdminAccountId { get; private set; }

    public ProvisioningStageState GetStageState(ProvisioningStage stage) => stage switch
    {
        ProvisioningStage.InfrastructureInstance => InfrastructureStageState,
        ProvisioningStage.ServiceDatabases => ServiceDatabasesStageState,
        ProvisioningStage.ClinicalPreparation => ClinicalStageState,
        ProvisioningStage.IdentityPreparation => IdentityStageState,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported provisioning stage.")
    };

    public ProvisioningStage? GetNextPendingStage()
    {
        foreach (var stage in ProvisioningStageExtensions.OrderedStages)
        {
            if (GetStageState(stage) != ProvisioningStageState.Confirmed)
            {
                return stage;
            }
        }

        return null;
    }

    public static bool TryValidateInitialAdmin(
        string? initialAdminFullName,
        string? initialAdminEmail,
        out string normalizedFullName,
        out string normalizedEmail,
        out IReadOnlyList<TenantValidationError> errors)
    {
        var validationErrors = new List<TenantValidationError>();

        normalizedFullName = AdministrativeValidation.ValidatePersonOrEntityName(
            initialAdminFullName,
            nameof(InitialAdminFullName),
            "InitialAdminFullName",
            InitialAdminFullNameMinLength,
            InitialAdminFullNameMaxLength,
            validationErrors);

        normalizedEmail = AdministrativeValidation.ValidateEmail(
            initialAdminEmail,
            nameof(InitialAdminEmail),
            "InitialAdminEmail",
            InitialAdminEmailMaxLength,
            validationErrors);

        errors = validationErrors;
        return validationErrors.Count == 0;
    }

    public static bool TryCreateInitial(
        Guid tenantId,
        string? initialAdminFullName,
        string? initialAdminEmail,
        DateTimeOffset createdAtUtc,
        out TenantProvisioningOperation? operation,
        out IReadOnlyList<TenantValidationError> errors)
    {
        var validationErrors = new List<TenantValidationError>();

        if (tenantId == Guid.Empty)
        {
            validationErrors.Add(new TenantValidationError(
                nameof(TenantId),
                "required",
                "TenantId is required."));
        }

        TryValidateInitialAdmin(
            initialAdminFullName,
            initialAdminEmail,
            out var normalizedFullName,
            out var normalizedEmail,
            out var adminErrors);

        validationErrors.AddRange(adminErrors);

        if (validationErrors.Count > 0)
        {
            operation = null;
            errors = validationErrors;
            return false;
        }

        var utcNow = createdAtUtc.ToUniversalTime();
        var operationId = Guid.CreateVersion7(utcNow);

        operation = new TenantProvisioningOperation(
            id: operationId,
            tenantId: tenantId,
            provisioningRequestId: operationId.ToString("D"),
            initialAdminFullName: normalizedFullName,
            initialAdminEmail: normalizedEmail,
            createdAtUtc: utcNow,
            concurrencyStamp: Guid.NewGuid());

        errors = [];
        return true;
    }

    /// <summary>
    /// Verifica que el payload asociado a <see cref="ProvisioningRequestId"/> permanezca estable
    /// entre reintentos para evitar conflictos silenciosos con Identity.
    /// </summary>
    public bool MatchesInitialAdminPayload(Guid tenantId, string? adminFullName, string? adminEmail) =>
        TenantId == tenantId
        && string.Equals(InitialAdminFullName, AdministrativeValidation.NormalizeName(adminFullName), StringComparison.Ordinal)
        && string.Equals(InitialAdminEmail, AdministrativeValidation.NormalizeEmail(adminEmail), StringComparison.Ordinal);

    public void RecordRecoverableFailure(string errorCode, string errorSummary, DateTimeOffset occurredAtUtc) =>
        RecordFailure(ProvisioningOperationStatus.FailedRetryable, errorCode, errorSummary, occurredAtUtc);

    public void RecordTerminalFailure(string errorCode, string errorSummary, DateTimeOffset occurredAtUtc) =>
        RecordFailure(ProvisioningOperationStatus.FailedTerminal, errorCode, errorSummary, occurredAtUtc);

    private void RecordFailure(
        ProvisioningOperationStatus targetStatus,
        string errorCode,
        string errorSummary,
        DateTimeOffset occurredAtUtc)
    {
        if (Status is ProvisioningOperationStatus.Completed or ProvisioningOperationStatus.FailedTerminal)
        {
            throw new InvalidOperationException(
                $"Cannot record a failure when provisioning operation is already in '{Status.ToCode()}' status.");
        }

        var normalizedCode = NormalizeErrorCode(errorCode);
        var sanitizedSummary = SanitizeDiagnosticSummary(errorSummary);
        var utcNow = occurredAtUtc.ToUniversalTime();

        if (Status != ProvisioningOperationStatus.InProgress)
        {
            AttemptCount++;
        }

        Status = targetStatus;
        LeaseOwner = null;
        LeaseUntilUtc = null;
        if (targetStatus == ProvisioningOperationStatus.FailedTerminal)
        {
            NextAttemptAtUtc = null;
            ManualInterventionRequired = true;
        }

        LastErrorCode = normalizedCode;
        LastErrorSummary = sanitizedSummary;
        LastAttemptAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
        ConcurrencyStamp = Guid.NewGuid();
    }

    internal static string NormalizeErrorCode(string errorCode)
    {
        var normalizedCode = errorCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!ErrorCodeRegex().IsMatch(normalizedCode))
        {
            throw new ArgumentException(
                $"Error code must contain {LastErrorCodeMinLength} to {LastErrorCodeMaxLength} ASCII uppercase characters, digits, underscores, or hyphens.",
                nameof(errorCode));
        }

        return normalizedCode;
    }

    internal static string SanitizeDiagnosticSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Error summary is required.", nameof(summary));
        }

        var normalizedWhitespace = ControlOrWhitespaceRunRegex()
            .Replace(summary, " ")
            .Trim();

        if (string.IsNullOrEmpty(normalizedWhitespace))
        {
            throw new ArgumentException("Error summary must contain printable text.", nameof(summary));
        }

        var redacted = ConnectionStringRegex().Replace(normalizedWhitespace, "[REDACTED_CONNECTION_STRING]");
        redacted = JwtTokenRegex().Replace(redacted, "[REDACTED_JWT]");
        redacted = BearerSecretRegex().Replace(redacted, "Bearer [REDACTED]");
        redacted = KeyValueSecretRegex().Replace(redacted, "${key}=[REDACTED]");

        return redacted.Length <= LastErrorSummaryMaxLength
            ? redacted
            : redacted[..LastErrorSummaryMaxLength];
    }

    internal static string? SanitizeTechnicalReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > TechnicalReferenceMaxLength)
        {
            throw new ArgumentException(
                $"Technical reference must not exceed {TechnicalReferenceMaxLength} characters.",
                nameof(value));
        }

        if (AdministrativeValidation.ContainsControlCharacters(trimmed)
            || ConnectionStringRegex().IsMatch(trimmed)
            || JwtTokenRegex().IsMatch(trimmed)
            || BearerSecretRegex().IsMatch(trimmed)
            || KeyValueSecretRegex().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "Technical reference contains disallowed control characters or sensitive patterns.",
                nameof(value));
        }

        return trimmed;
    }

    [GeneratedRegex("^[A-Z0-9_-]{2,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex("[\\p{C}\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex ControlOrWhitespaceRunRegex();

    [GeneratedRegex(
        "(?:Host|Server)\\s*=\\s*[^;\\s]+(?:\\s*;\\s*[A-Za-z_]+\\s*=\\s*[^;\\s]*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(
        "eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtTokenRegex();

    [GeneratedRegex("Bearer\\s+\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerSecretRegex();

    [GeneratedRegex(
        "(?<key>password|passwd|pwd|secret|token|api_key|apikey|x-internal-service-key|authorization|connectionstring)\\s*[:=]\\s*[^\\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();
}
