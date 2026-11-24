using System.Text.RegularExpressions;

namespace Saas.Platform.Api.Domain;

/// <summary>
/// Registro administrativo de una clínica (tenant) en el catálogo global de Platform.
/// Toda clínica recién registrada comienza en estado operativo <see cref="TenantOperationalState.PendienteAdmin"/>.
/// </summary>
public sealed partial class Tenant
{
    public const int PublicIdMinLength = 3;
    public const int PublicIdMaxLength = 64;
    public const int NameMinLength = 2;
    public const int NameMaxLength = 150;
    public const int ContactEmailMaxLength = 254;
    public const int OperationalStateMaxLength = 32;
    public const int MinMaxActiveAccounts = 1;

    private Tenant()
    {
    }

    private Tenant(
        Guid id,
        string publicId,
        string name,
        string contactEmail,
        int? maxActiveAccounts,
        TenantOperationalState operationalState,
        DateTimeOffset createdAtUtc,
        Guid concurrencyStamp)
    {
        Id = id;
        PublicId = publicId;
        Name = name;
        ContactEmail = contactEmail;
        MaxActiveAccounts = maxActiveAccounts;
        OperationalState = operationalState;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        ConcurrencyStamp = concurrencyStamp;
    }

    public Guid Id { get; private set; }

    public string PublicId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string ContactEmail { get; private set; } = string.Empty;

    public int? MaxActiveAccounts { get; private set; }

    public TenantOperationalState OperationalState { get; private set; } = TenantOperationalState.PendienteAdmin;

    public bool MayAuthenticate => OperationalState == TenantOperationalState.Activa;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public static bool TryRegister(
        string? publicId,
        string? name,
        string? contactEmail,
        int? maxActiveAccounts,
        DateTimeOffset createdAtUtc,
        out Tenant? tenant,
        out IReadOnlyList<TenantValidationError> errors)
    {
        var validationErrors = new List<TenantValidationError>();

        var normalizedPublicId = NormalizePublicId(publicId);
        if (string.IsNullOrEmpty(normalizedPublicId))
        {
            validationErrors.Add(new TenantValidationError(
                nameof(PublicId),
                "required",
                "PublicId is required."));
        }
        else if (normalizedPublicId.Length < PublicIdMinLength || normalizedPublicId.Length > PublicIdMaxLength)
        {
            validationErrors.Add(new TenantValidationError(
                nameof(PublicId),
                "length",
                $"PublicId must be between {PublicIdMinLength} and {PublicIdMaxLength} characters."));
        }
        else if (!PublicIdRegex().IsMatch(normalizedPublicId))
        {
            validationErrors.Add(new TenantValidationError(
                nameof(PublicId),
                "format",
                "PublicId must use lowercase ASCII letters, digits, and single internal hyphens (without dots or spaces)."));
        }

        var normalizedName = AdministrativeValidation.ValidatePersonOrEntityName(
            name,
            nameof(Name),
            "Name",
            NameMinLength,
            NameMaxLength,
            validationErrors);

        var normalizedEmail = AdministrativeValidation.ValidateEmail(
            contactEmail,
            nameof(ContactEmail),
            "ContactEmail",
            ContactEmailMaxLength,
            validationErrors);

        if (maxActiveAccounts is null)
        {
            validationErrors.Add(new TenantValidationError(
                nameof(MaxActiveAccounts),
                "required",
                "MaxActiveAccounts is required."));
        }
        else if (maxActiveAccounts < MinMaxActiveAccounts)
        {
            validationErrors.Add(new TenantValidationError(
                nameof(MaxActiveAccounts),
                "range",
                $"MaxActiveAccounts must be at least {MinMaxActiveAccounts}."));
        }

        if (validationErrors.Count > 0)
        {
            tenant = null;
            errors = validationErrors;
            return false;
        }

        var utcNow = createdAtUtc.ToUniversalTime();
        tenant = new Tenant(
            id: Guid.CreateVersion7(utcNow),
            publicId: normalizedPublicId,
            name: normalizedName,
            contactEmail: normalizedEmail,
            maxActiveAccounts: maxActiveAccounts,
            operationalState: TenantOperationalState.PendienteAdmin,
            createdAtUtc: utcNow,
            concurrencyStamp: Guid.NewGuid());

        errors = [];
        return true;
    }

    /// <summary>
    /// Evalúa si la clínica cumple todas las condiciones obligatorias para transicionar a ACTIVA.
    /// En PLAT-02A / PLAT-03A no ejecuta la transición efectiva; únicamente verifica invariantes de dominio.
    /// </summary>
    public bool CanActivate(
        TenantActivationPrerequisites prerequisites,
        out IReadOnlyList<TenantValidationError> unmetRequirements)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);

        var failures = new List<TenantValidationError>();

        if (OperationalState != TenantOperationalState.PendienteAdmin)
        {
            failures.Add(new TenantValidationError(
                nameof(OperationalState),
                "invalid_state",
                $"Tenant must be in '{TenantOperationalStateExtensions.PendienteAdminCode}' state before activation."));
        }

        if (MaxActiveAccounts is null or < MinMaxActiveAccounts)
        {
            failures.Add(new TenantValidationError(
                nameof(MaxActiveAccounts),
                "prerequisite_not_confirmed",
                $"A positive {nameof(MaxActiveAccounts)} quota (>= {MinMaxActiveAccounts}) must be configured before activation."));
        }

        if (!prerequisites.TenantDatabaseInstanceReady)
        {
            failures.Add(new TenantValidationError(
                nameof(TenantActivationPrerequisites.TenantDatabaseInstanceReady),
                "prerequisite_not_confirmed",
                "Dedicated tenant PostgreSQL instance readiness must be confirmed before activation."));
        }

        if (!prerequisites.ServiceDatabasesPrepared)
        {
            failures.Add(new TenantValidationError(
                nameof(TenantActivationPrerequisites.ServiceDatabasesPrepared),
                "prerequisite_not_confirmed",
                "Required per-service databases and migrations must be confirmed before activation."));
        }

        if (!prerequisites.IdentityInitialAdminReady)
        {
            failures.Add(new TenantValidationError(
                nameof(TenantActivationPrerequisites.IdentityInitialAdminReady),
                "prerequisite_not_confirmed",
                "Identity initial clinic administrator provisioning must be confirmed READY before activation."));
        }

        if (string.IsNullOrWhiteSpace(prerequisites.InitialAdminAccountId))
        {
            failures.Add(new TenantValidationError(
                nameof(TenantActivationPrerequisites.InitialAdminAccountId),
                "prerequisite_not_confirmed",
                "Confirmed InitialAdminAccountId from Identity is required before activation."));
        }

        unmetRequirements = failures;
        return failures.Count == 0;
    }

    public void UpdateAdministrativeName(string name, DateTimeOffset updatedAtUtc)
    {
        var normalizedName = AdministrativeValidation.NormalizeName(name);
        if (string.IsNullOrEmpty(normalizedName)
            || normalizedName.Length < NameMinLength
            || normalizedName.Length > NameMaxLength
            || AdministrativeValidation.ContainsControlCharacters(normalizedName))
        {
            throw new ArgumentException(
                $"Name must be between {NameMinLength} and {NameMaxLength} printable characters.",
                nameof(name));
        }

        Name = normalizedName;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        ConcurrencyStamp = Guid.NewGuid();
    }

    public static string NormalizePublicId(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    public static bool IsValidPublicIdFormat(string normalizedPublicId) =>
        !string.IsNullOrEmpty(normalizedPublicId)
        && normalizedPublicId.Length >= PublicIdMinLength
        && normalizedPublicId.Length <= PublicIdMaxLength
        && PublicIdRegex().IsMatch(normalizedPublicId);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicIdRegex();
}
