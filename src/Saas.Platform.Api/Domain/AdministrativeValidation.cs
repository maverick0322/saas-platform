using System.Net.Mail;

namespace Saas.Platform.Api.Domain;

internal static class AdministrativeValidation
{
    public static string NormalizeName(string? value) =>
        value?.Trim() ?? string.Empty;

    public static string NormalizeEmail(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    public static bool ContainsControlCharacters(string value) =>
        value.Any(char.IsControl);

    public static string ValidatePersonOrEntityName(
        string? rawValue,
        string fieldName,
        string displayName,
        int minLength,
        int maxLength,
        ICollection<TenantValidationError> errors)
    {
        var normalized = NormalizeName(rawValue);
        if (string.IsNullOrEmpty(normalized))
        {
            errors.Add(new TenantValidationError(
                fieldName,
                "required",
                $"{displayName} is required."));
        }
        else if (normalized.Length < minLength || normalized.Length > maxLength)
        {
            errors.Add(new TenantValidationError(
                fieldName,
                "length",
                $"{displayName} must be between {minLength} and {maxLength} characters."));
        }
        else if (ContainsControlCharacters(normalized))
        {
            errors.Add(new TenantValidationError(
                fieldName,
                "format",
                $"{displayName} must not contain control characters."));
        }

        return normalized;
    }

    public static string ValidateEmail(
        string? rawValue,
        string fieldName,
        string displayName,
        int maxLength,
        ICollection<TenantValidationError> errors)
    {
        var normalized = NormalizeEmail(rawValue);
        if (string.IsNullOrEmpty(normalized))
        {
            errors.Add(new TenantValidationError(
                fieldName,
                "required",
                $"{displayName} is required."));
        }
        else if (normalized.Length > maxLength)
        {
            errors.Add(new TenantValidationError(
                fieldName,
                "length",
                $"{displayName} must not exceed {maxLength} characters."));
        }
        else if (!IsValidEmail(normalized))
        {
            errors.Add(new TenantValidationError(
                fieldName,
                "format",
                $"{displayName} must be a valid email address."));
        }

        return normalized;
    }

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
}
