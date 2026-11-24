using System.Data.Common;
using Grpc.Core;
using Saas.Contracts.PlatformIdentity.V1;
using Saas.Platform.Api.Domain;
using DomainState = Saas.Platform.Api.Domain.TenantOperationalState;
using ProtoState = Saas.Contracts.PlatformIdentity.V1.TenantOperationalState;

namespace Saas.Platform.Api.Features.Tenants.GetTenantAccessState;

internal sealed class TenantAccessGrpcService(
    GetTenantAccessStateHandler handler,
    InternalGrpcOptions options,
    ILogger<TenantAccessGrpcService> logger)
    : TenantAccessService.TenantAccessServiceBase
{
    public override async Task<GetTenantAccessStateResponse> GetTenantAccessState(
        GetTenantAccessStateRequest request,
        ServerCallContext context)
    {
        if (!options.HasValidServiceKey())
        {
            logger.LogError(
                "Rejected gRPC GetTenantAccessState call (fail-closed) because InternalGrpc:InternalServiceKey is not configured securely.");
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Internal service authentication is not configured securely."));
        }

        if (!IsAuthenticated(context.RequestHeaders, options))
        {
            logger.LogWarning(
                "Rejected unauthenticated gRPC GetTenantAccessState call.");
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Missing or invalid x-internal-service-key credential."));
        }

        var query = ParseAndValidateQuery(request);

        GetTenantAccessStateResult result;
        try
        {
            result = await handler.HandleAsync(query, context.CancellationToken);
        }
        catch (Exception ex) when (IsPersistenceUnavailable(ex))
        {
            logger.LogWarning(
                "GetTenantAccessState failed closed due to persistence unavailability ({ExceptionType}).",
                ex.GetType().Name);
            throw new RpcException(new Status(
                StatusCode.Unavailable,
                "TENANT_STATE_UNAVAILABLE: Unable to verify tenant operational state."));
        }

        return result.Status switch
        {
            GetTenantAccessStateStatus.NotFound => throw new RpcException(new Status(
                StatusCode.NotFound,
                "TENANT_NOT_FOUND: No tenant exists for the requested identifier.")),

            GetTenantAccessStateStatus.QuotaNotConfigured => throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "TENANT_QUOTA_NOT_CONFIGURED: Tenant active account quota is not configured.")),

            GetTenantAccessStateStatus.Found when result.Tenant is not null => new GetTenantAccessStateResponse
            {
                TenantId = result.Tenant.TenantId.ToString("D"),
                Slug = result.Tenant.Slug,
                Name = result.Tenant.Name,
                State = MapOperationalState(result.Tenant.State),
                MayAuthenticate = result.Tenant.MayAuthenticate,
                MaxActiveAccounts = result.Tenant.MaxActiveAccounts,
                EvaluatedAtGmt6 = result.Tenant.EvaluatedAtGmt6
            },

            _ => throw new RpcException(new Status(
                StatusCode.Internal,
                "TENANT_STATE_ERROR: Unexpected lookup result."))
        };
    }

    private static bool IsAuthenticated(Metadata headers, InternalGrpcOptions options)
    {
        string? presentedKey = null;
        var count = 0;

        foreach (var entry in headers)
        {
            if (entry.IsBinary)
            {
                continue;
            }

            if (string.Equals(entry.Key, InternalGrpcOptions.HeaderName, StringComparison.OrdinalIgnoreCase))
            {
                presentedKey = entry.Value;
                count++;
                if (count > 1)
                {
                    return false;
                }
            }
        }

        return count == 1 && options.MatchesServiceKey(presentedKey);
    }

    private static GetTenantAccessStateQuery ParseAndValidateQuery(GetTenantAccessStateRequest request)
    {
        switch (request.LookupCase)
        {
            case GetTenantAccessStateRequest.LookupOneofCase.TenantId:
                if (!Guid.TryParse(request.TenantId, out var tenantId) || tenantId == Guid.Empty)
                {
                    throw new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        "tenant_id must be a valid non-empty UUID."));
                }

                return GetTenantAccessStateQuery.ByTenantId(tenantId);

            case GetTenantAccessStateRequest.LookupOneofCase.Slug:
                var normalizedSlug = Tenant.NormalizePublicId(request.Slug);
                if (!Tenant.IsValidPublicIdFormat(normalizedSlug))
                {
                    throw new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        "slug must be a valid tenant public identifier."));
                }

                return GetTenantAccessStateQuery.BySlug(normalizedSlug);

            default:
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "A valid tenant_id or slug lookup selector is required."));
        }
    }

    private static ProtoState MapOperationalState(DomainState state) => state switch
    {
        DomainState.PendienteAdmin => ProtoState.PendienteAdmin,
        DomainState.Activa => ProtoState.Activa,
        DomainState.Suspendida => ProtoState.Suspendida,
        _ => ProtoState.Unspecified
    };

    private static bool IsPersistenceUnavailable(Exception exception) =>
        exception is DbException or TimeoutException
        || (exception is InvalidOperationException && exception.InnerException is DbException or TimeoutException);
}
