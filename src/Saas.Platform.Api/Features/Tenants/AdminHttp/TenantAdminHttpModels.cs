using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.ListTenants;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTenantHttpRequest(
    string? PublicId,
    string? Name,
    string? ContactEmail,
    string? InitialAdminFullName,
    string? InitialAdminEmail,
    int? MaxActiveAccounts);

public sealed record ProvisioningStagesHttpResponse(
    string InfrastructureInstance,
    string ServiceDatabases,
    string ClinicalPreparation,
    string IdentityPreparation);

public sealed record TenantProvisioningSummaryHttpResponse(
    Guid OperationId,
    string ProvisioningRequestId,
    string Status,
    string InitialAdminFullName,
    string InitialAdminEmail,
    int AttemptCount,
    bool ManualInterventionRequired,
    string? LastErrorCode,
    string? LastErrorSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ProvisioningStagesHttpResponse Stages);

public sealed record TenantDetailsHttpResponse(
    Guid Id,
    string PublicId,
    string Name,
    string ContactEmail,
    string OperationalState,
    bool MayAuthenticate,
    int? MaxActiveAccounts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    TenantProvisioningSummaryHttpResponse? InitialProvisioning);

public sealed record TenantListItemHttpResponse(
    Guid Id,
    string PublicId,
    string Name,
    string OperationalState,
    bool MayAuthenticate,
    int? MaxActiveAccounts,
    string? ProvisioningStatus,
    bool ManualInterventionRequired,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record TenantListHttpResponse(
    IReadOnlyList<TenantListItemHttpResponse> Items,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages);

public sealed record ValidationErrorItemResponse(
    string Field,
    string Code,
    string Message);

public sealed record AdminProblemDetailsResponse(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    string Code,
    string TraceId);

public sealed record AdminValidationProblemDetailsResponse(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    string Code,
    string TraceId,
    IReadOnlyList<ValidationErrorItemResponse> Errors);

public sealed record AdminTenantConflictProblemDetailsResponse(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    string Code,
    string TraceId,
    Guid? ExistingTenantId);

internal static class AdminHttpJsonOptions
{
    public static JsonSerializerOptions Default { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.Strict,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow
        };

        options.Converters.Add(new Iso8601UtcDateTimeOffsetConverter());
        return options;
    }

    private sealed class Iso8601UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var raw = reader.GetString();
            return DateTimeOffset.Parse(raw!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}

internal static class TenantAdminHttpMapper
{
    public static TenantDetailsHttpResponse ToDetailsResponse(TenantDetails details) =>
        new(
            details.Id,
            details.PublicId,
            details.Name,
            details.ContactEmail,
            details.OperationalState.ToCode(),
            details.MayAuthenticate,
            details.MaxActiveAccounts,
            details.CreatedAtUtc,
            details.UpdatedAtUtc,
            details.InitialProvisioning is null
                ? null
                : new TenantProvisioningSummaryHttpResponse(
                    details.InitialProvisioning.OperationId,
                    details.InitialProvisioning.ProvisioningRequestId,
                    details.InitialProvisioning.Status.ToCode(),
                    details.InitialProvisioning.InitialAdminFullName,
                    details.InitialProvisioning.InitialAdminEmail,
                    details.InitialProvisioning.AttemptCount,
                    details.InitialProvisioning.ManualInterventionRequired,
                    details.InitialProvisioning.LastErrorCode,
                    ProvisioningAdministrativeErrorCatalog.ResolveControlledSummary(
                        details.InitialProvisioning.LastErrorCode,
                        details.InitialProvisioning.Status,
                        details.InitialProvisioning.ManualInterventionRequired),
                    details.InitialProvisioning.CreatedAtUtc,
                    details.InitialProvisioning.UpdatedAtUtc,
                    details.InitialProvisioning.LastAttemptAtUtc,
                    details.InitialProvisioning.NextAttemptAtUtc,
                    details.InitialProvisioning.CompletedAtUtc,
                    new ProvisioningStagesHttpResponse(
                        details.InitialProvisioning.InfrastructureStage.ToCode(),
                        details.InitialProvisioning.ServiceDatabasesStage.ToCode(),
                        details.InitialProvisioning.ClinicalStage.ToCode(),
                        details.InitialProvisioning.IdentityStage.ToCode())));

    public static TenantListHttpResponse ToListResponse(ListTenantsResult result) =>
        new(
            result.Items
                .Select(item => new TenantListItemHttpResponse(
                    item.Id,
                    item.PublicId,
                    item.Name,
                    item.OperationalState.ToCode(),
                    item.MayAuthenticate,
                    item.MaxActiveAccounts,
                    item.ProvisioningStatus?.ToCode(),
                    item.ManualInterventionRequired,
                    item.CreatedAtUtc,
                    item.UpdatedAtUtc))
                .ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount,
            result.TotalPages);

    public static string ToCamelCaseField(string domainFieldName)
    {
        if (string.IsNullOrEmpty(domainFieldName) || char.IsLower(domainFieldName[0]))
        {
            return domainFieldName;
        }

        return char.ToLowerInvariant(domainFieldName[0]) + domainFieldName[1..];
    }
}
