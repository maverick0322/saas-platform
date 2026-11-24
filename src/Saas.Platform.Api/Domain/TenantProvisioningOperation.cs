using System.Text.RegularExpressions;

namespace Saas.Platform.Api.Domain;

/// <summary>
/// Intención durable y reintentable de aprovisionamiento inicial para una clínica en la base global de Platform.
/// Conserva su identificador de solicitud (<see cref="ProvisioningRequestId"/>) y los datos del administrador inicial
/// de forma estable entre reintentos para preservar la idempotencia con Identity.
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

        var normalizedCode = errorCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!ErrorCodeRegex().IsMatch(normalizedCode))
        {
            throw new ArgumentException(
                $"Error code must contain {LastErrorCodeMinLength} to {LastErrorCodeMaxLength} ASCII uppercase characters, digits, underscores, or hyphens.",
                nameof(errorCode));
        }

        var sanitizedSummary = SanitizeDiagnosticSummary(errorSummary);
        var utcNow = occurredAtUtc.ToUniversalTime();

        Status = targetStatus;
        AttemptCount++;
        LastErrorCode = normalizedCode;
        LastErrorSummary = sanitizedSummary;
        LastAttemptAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
        ConcurrencyStamp = Guid.NewGuid();
    }

    private static string SanitizeDiagnosticSummary(string summary)
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

        var redacted = BearerSecretRegex().Replace(normalizedWhitespace, "Bearer [REDACTED]");
        redacted = KeyValueSecretRegex().Replace(redacted, "${key}=[REDACTED]");

        return redacted.Length <= LastErrorSummaryMaxLength
            ? redacted
            : redacted[..LastErrorSummaryMaxLength];
    }

    [GeneratedRegex("^[A-Z0-9_-]{2,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex("[\\p{C}\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex ControlOrWhitespaceRunRegex();

    [GeneratedRegex("Bearer\\s+\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerSecretRegex();

    [GeneratedRegex(
        "(?<key>password|passwd|pwd|secret|token|api_key|apikey|x-internal-service-key|authorization|connectionstring)\\s*[:=]\\s*[^\\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();
}
