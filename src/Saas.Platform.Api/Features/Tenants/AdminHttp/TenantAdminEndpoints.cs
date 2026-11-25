using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.ListTenants;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

internal static class TenantAdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminTenantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/tenants")
            .RequireAuthorization(AdminAuthPolicies.SuperAdmin);

        group.MapPost(string.Empty, RegisterTenantAsync);
        group.MapGet(string.Empty, ListTenantsAsync);
        group.MapGet("/{tenantId}", GetTenantByIdAsync);

        return endpoints;
    }

    private static async Task RegisterTenantAsync(
        HttpContext httpContext,
        IIdentityAccountVerifier identityVerifier,
        RegisterTenantHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!AdminIdentityClaimsValidator.TryGetSubjectId(httpContext.User, out var actorSubjectId)
            || !TryExtractBearerToken(httpContext.Request, out var bearerToken))
        {
            await AdminProblemDetailsWriter.WriteUnauthorizedAsync(httpContext);
            return;
        }

        var (parseSuccess, requestModel, parseErrors) = await TryReadCreateRequestAsync(httpContext.Request, cancellationToken);
        if (!parseSuccess || requestModel is null)
        {
            await AdminProblemDetailsWriter.WriteValidationAsync(
                httpContext,
                "https://vital.internal/problems/invalid-request-body",
                "Invalid request body",
                "The request body must be a valid JSON object matching CreateTenantRequest.",
                "INVALID_REQUEST_BODY",
                parseErrors);
            return;
        }

        var verificationOutcome = await identityVerifier.VerifySuperAdminAsync(
            actorSubjectId,
            bearerToken,
            cancellationToken);

        switch (verificationOutcome)
        {
            case IdentityAccountVerificationOutcome.Unauthorized:
                await AdminProblemDetailsWriter.WriteUnauthorizedAsync(httpContext);
                return;
            case IdentityAccountVerificationOutcome.Forbidden:
                await AdminProblemDetailsWriter.WriteForbiddenAsync(httpContext);
                return;
            case IdentityAccountVerificationOutcome.Unavailable:
                await AdminProblemDetailsWriter.WriteServiceUnavailableAsync(httpContext);
                return;
            case IdentityAccountVerificationOutcome.Verified:
                break;
            default:
                await AdminProblemDetailsWriter.WriteServiceUnavailableAsync(httpContext);
                return;
        }

        RegisterTenantResult result;
        try
        {
            result = await handler.HandleAsync(
                new RegisterTenantCommand(
                    requestModel.PublicId,
                    requestModel.Name,
                    requestModel.ContactEmail,
                    requestModel.InitialAdminFullName,
                    requestModel.InitialAdminEmail,
                    requestModel.MaxActiveAccounts),
                cancellationToken);
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsPostgreSqlUnavailable(ex))
        {
            loggerFactory.CreateLogger("TenantAdminEndpoints").LogWarning(
                "PostgreSQL unavailable during administrative tenant registration by actor {ActorSubjectId} (TraceId={TraceId}).",
                actorSubjectId,
                AdminProblemDetailsWriter.ResolveTraceId(httpContext));
            await AdminProblemDetailsWriter.WriteServiceUnavailableAsync(httpContext);
            return;
        }

        var logger = loggerFactory.CreateLogger("TenantAdminEndpoints");
        var traceId = AdminProblemDetailsWriter.ResolveTraceId(httpContext);

        switch (result.Status)
        {
            case RegisterTenantStatus.Created when result.Tenant is not null:
            {
                var responseBody = TenantAdminHttpMapper.ToDetailsResponse(result.Tenant);
                logger.LogInformation(
                    "Administrative tenant created by ActorSubjectId={ActorSubjectId} TenantId={TenantId} PublicId={PublicId} OperationId={OperationId} TraceId={TraceId} CreatedAtUtc={CreatedAtUtc}.",
                    actorSubjectId,
                    responseBody.Id,
                    responseBody.PublicId,
                    responseBody.InitialProvisioning?.OperationId,
                    traceId,
                    responseBody.CreatedAtUtc);

                httpContext.Response.StatusCode = StatusCodes.Status201Created;
                httpContext.Response.Headers.Location = $"/v1/tenants/{responseBody.Id:D}";
                await httpContext.Response.WriteAsJsonAsync(
                    responseBody,
                    AdminHttpJsonOptions.Default,
                    "application/json; charset=utf-8",
                    cancellationToken);
                return;
            }

            case RegisterTenantStatus.ValidationFailed:
            {
                var errors = result.ValidationErrors
                    .Select(err => new ValidationErrorItemResponse(
                        TenantAdminHttpMapper.ToCamelCaseField(err.Field),
                        err.Code,
                        err.Message))
                    .ToArray();

                await AdminProblemDetailsWriter.WriteValidationAsync(
                    httpContext,
                    "https://vital.internal/problems/validation-error",
                    "Validation failed",
                    "One or more validation errors occurred while processing the tenant registration.",
                    "VALIDATION_ERROR",
                    errors);
                return;
            }

            case RegisterTenantStatus.Conflict when result.ConflictKind == RegisterTenantConflictKind.PublicIdAlreadyExists:
            {
                var normalizedSlug = Tenant.NormalizePublicId(requestModel.PublicId);
                logger.LogWarning(
                    "Administrative tenant registration conflict by ActorSubjectId={ActorSubjectId} PublicId={PublicId} ExistingTenantId={ExistingTenantId} TraceId={TraceId}.",
                    actorSubjectId,
                    normalizedSlug,
                    result.ExistingTenantId,
                    traceId);

                await AdminProblemDetailsWriter.WriteConflictAsync(httpContext, normalizedSlug, result.ExistingTenantId);
                return;
            }

            case RegisterTenantStatus.Conflict:
            default:
                logger.LogError(
                    "Unexpected internal conflict during administrative tenant registration by ActorSubjectId={ActorSubjectId} TraceId={TraceId}.",
                    actorSubjectId,
                    traceId);
                await AdminProblemDetailsWriter.WriteInternalErrorAsync(httpContext);
                return;
        }
    }

    private static async Task ListTenantsAsync(
        HttpContext httpContext,
        [FromServices] ListTenantsHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryParsePagination(httpContext.Request.Query, out var page, out var pageSize, out var paginationErrors))
        {
            await AdminProblemDetailsWriter.WriteValidationAsync(
                httpContext,
                "https://vital.internal/problems/invalid-pagination",
                "Invalid pagination parameters",
                $"Pagination parameters must satisfy page >= 1 and 1 <= pageSize <= {ListTenantsQuery.MaxPageSize}.",
                "INVALID_PAGINATION",
                paginationErrors);
            return;
        }

        ListTenantsResult result;
        try
        {
            result = await handler.HandleAsync(new ListTenantsQuery(page, pageSize), cancellationToken);
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsPostgreSqlUnavailable(ex))
        {
            loggerFactory.CreateLogger("TenantAdminEndpoints").LogWarning(
                "PostgreSQL unavailable during administrative tenant listing (TraceId={TraceId}).",
                AdminProblemDetailsWriter.ResolveTraceId(httpContext));
            await AdminProblemDetailsWriter.WriteServiceUnavailableAsync(httpContext);
            return;
        }

        var responseBody = TenantAdminHttpMapper.ToListResponse(result);
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        await httpContext.Response.WriteAsJsonAsync(
            responseBody,
            AdminHttpJsonOptions.Default,
            "application/json; charset=utf-8",
            cancellationToken);
    }

    private static async Task GetTenantByIdAsync(
        string tenantId,
        HttpContext httpContext,
        GetTenantByIdHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(tenantId, out var parsedTenantId) || parsedTenantId == Guid.Empty)
        {
            await AdminProblemDetailsWriter.WriteInvalidTenantIdAsync(httpContext);
            return;
        }

        TenantDetails? details;
        try
        {
            details = await handler.HandleAsync(new GetTenantByIdQuery(parsedTenantId), cancellationToken);
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsPostgreSqlUnavailable(ex))
        {
            loggerFactory.CreateLogger("TenantAdminEndpoints").LogWarning(
                "PostgreSQL unavailable while querying tenant {TenantId} (TraceId={TraceId}).",
                parsedTenantId,
                AdminProblemDetailsWriter.ResolveTraceId(httpContext));
            await AdminProblemDetailsWriter.WriteServiceUnavailableAsync(httpContext);
            return;
        }

        if (details is null)
        {
            await AdminProblemDetailsWriter.WriteNotFoundAsync(httpContext);
            return;
        }

        var responseBody = TenantAdminHttpMapper.ToDetailsResponse(details);
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        await httpContext.Response.WriteAsJsonAsync(
            responseBody,
            AdminHttpJsonOptions.Default,
            "application/json; charset=utf-8",
            cancellationToken);
    }

    private static bool TryExtractBearerToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        if (!request.Headers.TryGetValue("Authorization", out var headerValues) || headerValues.Count != 1)
        {
            return false;
        }

        var rawHeader = headerValues[0];
        if (string.IsNullOrWhiteSpace(rawHeader)
            || !rawHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = rawHeader["Bearer ".Length..].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    private static async Task<(bool Success, CreateTenantHttpRequest? Request, IReadOnlyList<ValidationErrorItemResponse> Errors)>
        TryReadCreateRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentType is null
            || !request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return (
                false,
                null,
                [new ValidationErrorItemResponse("body", "content_type", "Content-Type must be application/json.")]);
        }

        try
        {
            var parsed = await JsonSerializer.DeserializeAsync<CreateTenantHttpRequest>(
                request.Body,
                AdminHttpJsonOptions.Default,
                cancellationToken);

            if (parsed is null)
            {
                return (
                    false,
                    null,
                    [new ValidationErrorItemResponse("body", "required", "Request body cannot be null or empty.")]);
            }

            return (true, parsed, []);
        }
        catch (JsonException)
        {
            return (
                false,
                null,
                [new ValidationErrorItemResponse("body", "format", "Request body contains malformed JSON, unknown fields, or incompatible value types.")]);
        }
    }

    private static bool TryParsePagination(
        IQueryCollection query,
        out int page,
        out int pageSize,
        out IReadOnlyList<ValidationErrorItemResponse> errors)
    {
        page = ListTenantsQuery.DefaultPage;
        pageSize = ListTenantsQuery.DefaultPageSize;
        var errorList = new List<ValidationErrorItemResponse>();

        if (query.TryGetValue("page", out var pageValues))
        {
            if (pageValues.Count != 1
                || !int.TryParse(pageValues[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out page)
                || page < 1)
            {
                errorList.Add(new ValidationErrorItemResponse(
                    "page",
                    "range",
                    "page must be an integer between 1 and 2147483647."));
            }
        }

        if (query.TryGetValue("pageSize", out var pageSizeValues))
        {
            if (pageSizeValues.Count != 1
                || !int.TryParse(pageSizeValues[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1
                || pageSize > ListTenantsQuery.MaxPageSize)
            {
                errorList.Add(new ValidationErrorItemResponse(
                    "pageSize",
                    "range",
                    $"pageSize must be between 1 and {ListTenantsQuery.MaxPageSize}."));
            }
        }

        errors = errorList;
        return errorList.Count == 0;
    }

    public static void ConfigureJwtBearerEvents(JwtBearerOptions options)
    {
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                AdminIdentityClaimsValidator.Validate(context);
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await AdminProblemDetailsWriter.WriteUnauthorizedAsync(context.HttpContext);
            },
            OnForbidden = async context =>
            {
                await AdminProblemDetailsWriter.WriteForbiddenAsync(context.HttpContext);
            }
        };
    }
}
