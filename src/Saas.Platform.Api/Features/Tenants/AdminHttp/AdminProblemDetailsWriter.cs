using System.Diagnostics;
using System.Net.Sockets;
using Npgsql;

namespace Saas.Platform.Api.Features.Tenants.AdminHttp;

internal static class DatabaseExceptionClassifier
{
    public static bool IsPostgreSqlUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException pgEx:
                    if (IsConnectionOrOperatorSqlState(pgEx.SqlState))
                    {
                        return true;
                    }

                    return false;

                case NpgsqlException:
                case SocketException:
                case TimeoutException:
                    return true;
            }
        }

        return false;
    }

    private static bool IsConnectionOrOperatorSqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState))
        {
            return false;
        }

        return sqlState.StartsWith("08", StringComparison.Ordinal)
            || sqlState.StartsWith("57P", StringComparison.Ordinal)
            || string.Equals(sqlState, "53300", StringComparison.Ordinal)
            || string.Equals(sqlState, "53400", StringComparison.Ordinal);
    }
}

internal static class AdminProblemDetailsWriter
{
    private const string ProblemContentType = "application/problem+json; charset=utf-8";

    public static Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.Headers["WWW-Authenticate"] = "Bearer";
        return WriteProblemAsync(
            context,
            StatusCodes.Status401Unauthorized,
            "https://vital.internal/problems/unauthorized",
            "Unauthorized",
            "A valid RS256 Bearer token issued by saas-identity with coherent identity claims is required.",
            "UNAUTHORIZED");
    }

    public static Task WriteForbiddenAsync(HttpContext context) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status403Forbidden,
            "https://vital.internal/problems/forbidden",
            "Forbidden",
            "Only active global SUPER_ADMIN accounts are authorized to access administrative tenant endpoints.",
            "FORBIDDEN");

    public static Task WriteNotFoundAsync(HttpContext context) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status404NotFound,
            "https://vital.internal/problems/tenant-not-found",
            "Tenant not found",
            "No tenant was found for the specified identifier.",
            "TENANT_NOT_FOUND");

    public static Task WriteInvalidTenantIdAsync(HttpContext context) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status400BadRequest,
            "https://vital.internal/problems/invalid-tenant-id",
            "Invalid tenant identifier",
            "The tenantId path parameter must be a non-empty valid UUID.",
            "INVALID_TENANT_ID");

    public static Task WriteServiceUnavailableAsync(HttpContext context) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "https://vital.internal/problems/service-unavailable",
            "Service unavailable",
            "A required dependency is temporarily unavailable to complete the administrative operation safely.",
            "SERVICE_UNAVAILABLE");

    public static Task WriteInternalErrorAsync(HttpContext context) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "https://vital.internal/problems/internal-error",
            "Internal error",
            "An internal error prevented completing the administrative operation.",
            "INTERNAL_ERROR");

    public static Task WriteValidationAsync(
        HttpContext context,
        string type,
        string title,
        string detail,
        string code,
        IReadOnlyList<ValidationErrorItemResponse> errors)
    {
        var payload = new AdminValidationProblemDetailsResponse(
            type,
            title,
            StatusCodes.Status400BadRequest,
            detail,
            ResolveInstance(context),
            code,
            ResolveTraceId(context),
            errors);

        return WriteJsonPayloadAsync(context, StatusCodes.Status400BadRequest, payload);
    }

    public static Task WriteConflictAsync(HttpContext context, string publicId, Guid? existingTenantId)
    {
        var payload = new AdminTenantConflictProblemDetailsResponse(
            "https://vital.internal/problems/tenant-public-id-conflict",
            "Tenant public identifier conflict",
            StatusCodes.Status409Conflict,
            $"A tenant with public identifier '{publicId}' is already registered.",
            ResolveInstance(context),
            "TENANT_PUBLIC_ID_CONFLICT",
            ResolveTraceId(context),
            existingTenantId);

        return WriteJsonPayloadAsync(context, StatusCodes.Status409Conflict, payload);
    }

    private static Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string type,
        string title,
        string detail,
        string code)
    {
        var payload = new AdminProblemDetailsResponse(
            type,
            title,
            statusCode,
            detail,
            ResolveInstance(context),
            code,
            ResolveTraceId(context));

        return WriteJsonPayloadAsync(context, statusCode, payload);
    }

    private static Task WriteJsonPayloadAsync<T>(HttpContext context, int statusCode, T payload)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ProblemContentType;
        return context.Response.WriteAsJsonAsync(payload, AdminHttpJsonOptions.Default, ProblemContentType, context.RequestAborted);
    }

    public static string ResolveTraceId(HttpContext context) =>
        Activity.Current?.Id ?? context.TraceIdentifier;

    private static string ResolveInstance(HttpContext context) =>
        context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
}
