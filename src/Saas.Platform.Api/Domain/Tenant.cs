using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Saas.Platform.Api.Domain;

/// <summary>
/// Registro administrativo mínimo de una clínica (tenant) en el catálogo global de Platform.
/// No representa por sí solo una clínica operativamente habilitada ni incluye estados candidatos
/// pendientes de ratificación.
/// </summary>
public sealed partial class Tenant
{
    public const int PublicIdMinLength = 3;
    public const int PublicIdMaxLength = 64;
    public const int NameMinLength = 2;
    public const int NameMaxLength = 150;
    public const int ContactEmailMaxLength = 254;

    private Tenant()
    {
    }

    private Tenant(
        Guid id,
        string publicId,
        string name,
        string contactEmail,
        DateTimeOffset createdAtUtc,
        Guid concurrencyStamp)
    {
        Id = id;
        PublicId = publicId;
        Name = name;
        ContactEmail = contactEmail;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        ConcurrencyStamp = concurrencyStamp;
    }

    public Guid Id { get; private set; }

    public string PublicId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string ContactEmail { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public static bool TryRegister(
        string? publicId,
        string? name,
        string? contactEmail,
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

        var normalizedName = NormalizeName(name);
        if (string.IsNullOrEmpty(normalizedName))
        {
            validationErrors.Add(new TenantValidationError(
                nameof(Name),
                "required",
                "Name is required."));
        }
        else if (normalizedName.Length < NameMinLength || normalizedName.Length > NameMaxLength)
        {
            validationErrors.Add(new TenantValidationError(
                nameof(Name),
                "length",
                $"Name must be between {NameMinLength} and {NameMaxLength} characters."));
        }
        else if (ContainsControlCharacters(normalizedName))
        {
            validationErrors.Add(new TenantValidationError(
                nameof(Name),
                "format",
                "Name must not contain control characters."));
        }

        var normalizedEmail = NormalizeEmail(contactEmail);
        if (string.IsNullOrEmpty(normalizedEmail))
        {
            validationErrors.Add(new TenantValidationError(
                nameof(ContactEmail),
                "required",
                "ContactEmail is required."));
        }
        else if (normalizedEmail.Length > ContactEmailMaxLength)
        {
            validationErrors.Add(new TenantValidationError(
                nameof(ContactEmail),
                "length",
                $"ContactEmail must not exceed {ContactEmailMaxLength} characters."));
        }
        else if (!IsValidEmail(normalizedEmail))
        {
            validationErrors.Add(new TenantValidationError(
                nameof(ContactEmail),
                "format",
                "ContactEmail must be a valid email address."));
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
            createdAtUtc: utcNow,
            concurrencyStamp: Guid.NewGuid());

        errors = [];
        return true;
    }

    public void UpdateAdministrativeName(string name, DateTimeOffset updatedAtUtc)
    {
        var normalizedName = NormalizeName(name);
        if (string.IsNullOrEmpty(normalizedName)
            || normalizedName.Length < NameMinLength
            || normalizedName.Length > NameMaxLength
            || ContainsControlCharacters(normalizedName))
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

    private static string NormalizeName(string? value) =>
        value?.Trim() ?? string.Empty;

    private static string NormalizeEmail(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool ContainsControlCharacters(string value) =>
        value.Any(char.IsControl);

    private static bool IsValidEmail(string value)
    {
        if (value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            var address = new MailAddress(value);
            if (!string.Equals(address.Address, value, StringComparison.Ordinal))
            {
                return false;
            }

            var atIndex = value.IndexOf('@');
            if (atIndex <= 0 || atIndex >= value.Length - 3)
            {
                return false;
            }

            var domain = value[(atIndex + 1)..];
            return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicIdRegex();
}
