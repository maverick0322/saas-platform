using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saas.Contracts.PlatformIdentity.V1;
using Saas.Platform.Api;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.GetTenantAccessState;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Xunit;
using DomainState = Saas.Platform.Api.Domain.TenantOperationalState;
using ProtoState = Saas.Contracts.PlatformIdentity.V1.TenantOperationalState;

namespace Saas.Platform.Tests;

public sealed class TenantAccessGrpcIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>
{
    private const string SyntheticTestServiceKey = "SyntheticPlat03ATestServiceKey!2026";

    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 24, 18, 30, 15, TimeSpan.Zero);

    private static readonly Method<GetTenantAccessStateRequest, GetTenantAccessStateResponse> GetTenantAccessStateMethod =
        new(
            MethodType.Unary,
            "vital.platform_identity.v1.TenantAccessService",
            "GetTenantAccessState",
            Marshallers.Create(
                request => request.ToByteArray(),
                bytes => GetTenantAccessStateRequest.Parser.ParseFrom(bytes)),
            Marshallers.Create(
                response => response.ToByteArray(),
                bytes => GetTenantAccessStateResponse.Parser.ParseFrom(bytes)));

    [Fact]
    public async Task GetTenantAccessState_ResolvesByUuidAndNormalizedSlug_WithDeterministicGmt6AndMinimalPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("resolve", ct);
        var clock = new MutableTimeProvider(FixedUtc);

        var registered = await RegisterSyntheticTenantAsync(
            dbConnectionString,
            "clinica-norte-01",
            "Clínica Sintética Norte",
            maxActiveAccounts: 18,
            clock,
            ct);

        await using var host = await StartGrpcApiHostAsync(dbConnectionString, clock, ct);

        // 1. Lookup by UUID
        var byUuid = await host.CallAsync(
            new GetTenantAccessStateRequest { TenantId = registered.Id.ToString("D") },
            SyntheticTestServiceKey,
            ct);

        Assert.Equal(registered.Id.ToString("D"), byUuid.TenantId);
        Assert.Equal("clinica-norte-01", byUuid.Slug);
        Assert.Equal("Clínica Sintética Norte", byUuid.Name);
        Assert.Equal(ProtoState.PendienteAdmin, byUuid.State);
        Assert.False(byUuid.MayAuthenticate);
        Assert.Equal(18, byUuid.MaxActiveAccounts);
        Assert.Equal("2026-11-24T12:30:15.0000000-06:00", byUuid.EvaluatedAtGmt6);

        // 2. Lookup by unnormalized slug
        var bySlug = await host.CallAsync(
            new GetTenantAccessStateRequest { Slug = "  CLINICA-NORTE-01  " },
            SyntheticTestServiceKey,
            ct);

        Assert.Equal(byUuid, bySlug);

        // Verify response descriptor only contains the 7 frozen contract fields (no contact/admin/clinical data)
        var fieldNames = GetTenantAccessStateResponse.Descriptor.Fields.InDeclarationOrder()
            .Select(f => f.Name)
            .ToArray();
        Assert.Equal(
            [
                "tenant_id",
                "slug",
                "name",
                "state",
                "may_authenticate",
                "max_active_accounts",
                "evaluated_at_gmt6"
            ],
            fieldNames);

        // HTTP technical endpoints remain functional on the separate HTTP/1.1 port
        using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.HttpPort}") };
        var liveResponse = await httpClient.GetAsync("/health/live", ct);
        var readyResponse = await httpClient.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);

        using var liveDoc = JsonDocument.Parse(await liveResponse.Content.ReadAsStringAsync(ct));
        Assert.Equal("Healthy", liveDoc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetTenantAccessState_MapsOperationalStatesAndDoesNotMixDifferentTenantsOrQuotas()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("states_quotas", ct);
        var clock = new MutableTimeProvider(FixedUtc);

        var pending = await RegisterSyntheticTenantAsync(
            dbConnectionString,
            "clinica-pendiente-01",
            "Clínica Pendiente",
            maxActiveAccounts: 10,
            clock,
            ct);

        var active = await RegisterSyntheticTenantAsync(
            dbConnectionString,
            "clinica-activa-02",
            "Clínica Activa",
            maxActiveAccounts: 42,
            clock,
            ct);

        var suspended = await RegisterSyntheticTenantAsync(
            dbConnectionString,
            "clinica-suspendida-03",
            "Clínica Suspendida",
            maxActiveAccounts: 7,
            clock,
            ct);

        // Synthetic test fixture updates operational states directly in SQL (no production API mutation route).
        await SetOperationalStateForTestAsync(dbConnectionString, active.Id, DomainState.Activa, ct);
        await SetOperationalStateForTestAsync(dbConnectionString, suspended.Id, DomainState.Suspendida, ct);

        await using var host = await StartGrpcApiHostAsync(dbConnectionString, clock, ct);

        var pendingResp = await host.CallAsync(
            new GetTenantAccessStateRequest { Slug = "clinica-pendiente-01" },
            SyntheticTestServiceKey,
            ct);
        Assert.Equal(pending.Id.ToString("D"), pendingResp.TenantId);
        Assert.Equal(ProtoState.PendienteAdmin, pendingResp.State);
        Assert.False(pendingResp.MayAuthenticate);
        Assert.Equal(10, pendingResp.MaxActiveAccounts);

        var activeResp = await host.CallAsync(
            new GetTenantAccessStateRequest { TenantId = active.Id.ToString("D") },
            SyntheticTestServiceKey,
            ct);
        Assert.Equal(active.Id.ToString("D"), activeResp.TenantId);
        Assert.Equal("clinica-activa-02", activeResp.Slug);
        Assert.Equal(ProtoState.Activa, activeResp.State);
        Assert.True(activeResp.MayAuthenticate);
        Assert.Equal(42, activeResp.MaxActiveAccounts);

        var suspendedResp = await host.CallAsync(
            new GetTenantAccessStateRequest { Slug = "clinica-suspendida-03" },
            SyntheticTestServiceKey,
            ct);
        Assert.Equal(suspended.Id.ToString("D"), suspendedResp.TenantId);
        Assert.Equal(ProtoState.Suspendida, suspendedResp.State);
        Assert.False(suspendedResp.MayAuthenticate);
        Assert.Equal(7, suspendedResp.MaxActiveAccounts);
    }

    [Fact]
    public async Task GetTenantAccessState_MissingOrWrongServiceKey_ReturnsUnauthenticatedEvenWhenTenantExists()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("auth_reject", ct);
        var clock = new MutableTimeProvider(FixedUtc);

        var tenant = await RegisterSyntheticTenantAsync(
            dbConnectionString,
            "clinica-auth-01",
            "Clínica Auth",
            maxActiveAccounts: 15,
            clock,
            ct);
        await SetOperationalStateForTestAsync(dbConnectionString, tenant.Id, DomainState.Activa, ct);

        await using var host = await StartGrpcApiHostAsync(dbConnectionString, clock, ct);

        // 1. Missing metadata header
        var missingEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { Slug = "clinica-auth-01" }, serviceKey: null, ct));
        Assert.Equal(StatusCode.Unauthenticated, missingEx.StatusCode);

        // 2. Wrong secret on existing active tenant
        var wrongEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { TenantId = tenant.Id.ToString("D") }, "WrongKeyValue123456!", ct));
        Assert.Equal(StatusCode.Unauthenticated, wrongEx.StatusCode);

        // 3. Wrong secret on non-existent tenant (auth checked before lookup)
        var wrongNonExistentEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { Slug = "clinica-inexistente" }, "WrongKeyValue123456!", ct));
        Assert.Equal(StatusCode.Unauthenticated, wrongNonExistentEx.StatusCode);
    }

    [Fact]
    public async Task GetTenantAccessState_NotFoundAndInvalidSelectors_ReturnExpectedGrpcStatusCodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("errors", ct);
        var clock = new MutableTimeProvider(FixedUtc);

        await using var host = await StartGrpcApiHostAsync(dbConnectionString, clock, ct);

        var notFoundUuidEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { TenantId = Guid.NewGuid().ToString("D") }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.NotFound, notFoundUuidEx.StatusCode);

        var notFoundSlugEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { Slug = "clinica-que-no-existe" }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.NotFound, notFoundSlugEx.StatusCode);

        var invalidUuidEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { TenantId = "no-es-uuid" }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.InvalidArgument, invalidUuidEx.StatusCode);

        var emptyUuidEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { TenantId = Guid.Empty.ToString("D") }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.InvalidArgument, emptyUuidEx.StatusCode);

        var invalidSlugEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { Slug = "clinica con espacios" }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.InvalidArgument, invalidSlugEx.StatusCode);

        var missingSelectorEx = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest(), SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.InvalidArgument, missingSelectorEx.StatusCode);
    }

    [Fact]
    public async Task GetTenantAccessState_MissingOrNullQuota_FailsClosedWithFailedPrecondition()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await CreateMigratedIsolatedDatabaseAsync("missing_quota", ct);
        var clock = new MutableTimeProvider(FixedUtc);

        var legacyTenantId = Guid.CreateVersion7(FixedUtc);
        await using (var conn = new NpgsqlConnection(dbConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO platform.tenants
                    (id, public_id, name, contact_email, max_active_accounts, operational_state, created_at_utc, updated_at_utc, concurrency_stamp)
                VALUES
                    (@id, 'clinica-sin-cuota', 'Clínica Sin Cuota', 'contacto@sincuota.test', NULL, 'ACTIVA', @now, @now, @stamp);
                """,
                conn);
            cmd.Parameters.AddWithValue("id", legacyTenantId);
            cmd.Parameters.AddWithValue("now", FixedUtc);
            cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var host = await StartGrpcApiHostAsync(dbConnectionString, clock, ct);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { TenantId = legacyTenantId.ToString("D") }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task GetTenantAccessState_PostgreSqlUnavailable_FailsClosedWithUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new MutableTimeProvider(FixedUtc);

        // Allocate a guaranteed closed local port for unreachable PostgreSQL.
        int closedPort;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var brokenDb = $"Host=127.0.0.1;Port={closedPort};Database=unavailable_db;Username=postgres;Timeout=1;Command Timeout=1";
        await using var host = await StartGrpcApiHostAsync(brokenDb, clock, ct);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { Slug = "clinica-cualquiera" }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public void EnablingInternalGrpcWithoutValidKey_FailsFastAtStartup()
    {
        var (httpPort, grpcPort) = AllocateTwoDistinctPorts();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlatformHostFactory.CreateApiApplication(
                [
                    $"--ConnectionStrings:PlatformDatabase={fixture.ConnectionString}",
                    $"--Ports:Http={httpPort}",
                    "--InternalGrpc:Enabled=true",
                    $"--InternalGrpc:Port={grpcPort}",
                    "--InternalGrpc:InternalServiceKey="
                ]));

        Assert.Contains("InternalServiceKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradeMigration_FromPlat02CSchema_PreservesLegacyRowsWithoutAutoActivatingOrInventingQuota()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConnectionString = await fixture.CreateEmptyDatabaseAsync(
            $"plat03a_upgrade_{Guid.NewGuid():N}",
            ct);

        var legacyTenantId = Guid.CreateVersion7(FixedUtc);
        const string legacySlug = "clinica-plat02c-legado";

        await using (var preUpgradeContext = fixture.CreateDbContext(dbConnectionString))
        {
            var migrator = preUpgradeContext.GetService<IMigrator>();
            await migrator.MigrateAsync("20261124070500_AddProvisioningWorkerLeaseAndStageProgress", ct);

            await using var conn = new NpgsqlConnection(dbConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO platform.tenants
                    (id, public_id, name, contact_email, operational_state, created_at_utc, updated_at_utc, concurrency_stamp)
                VALUES
                    (@id, @slug, 'Clínica Previa PLAT-02C', 'previa@clinica.test', 'PENDIENTE_ADMIN', @now, @now, @stamp);
                """,
                conn);
            cmd.Parameters.AddWithValue("id", legacyTenantId);
            cmd.Parameters.AddWithValue("slug", legacySlug);
            cmd.Parameters.AddWithValue("now", FixedUtc);
            cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var upgradedContext = fixture.CreateDbContext(dbConnectionString);
        await upgradedContext.Database.MigrateAsync(ct);

        Assert.Empty(await upgradedContext.Database.GetPendingMigrationsAsync(ct));
        Assert.False(upgradedContext.Database.HasPendingModelChanges());

        var details = await new GetTenantByIdHandler(upgradedContext)
            .HandleAsync(new GetTenantByIdQuery(legacyTenantId), ct);

        Assert.NotNull(details);
        Assert.Equal(DomainState.PendienteAdmin, details.OperationalState);
        Assert.False(details.MayAuthenticate);
        Assert.Null(details.MaxActiveAccounts);

        var clock = new MutableTimeProvider(FixedUtc);
        await using var host = await StartGrpcApiHostAsync(dbConnectionString, clock, ct);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            host.CallAsync(new GetTenantAccessStateRequest { Slug = legacySlug }, SyntheticTestServiceKey, ct));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    private async Task<string> CreateMigratedIsolatedDatabaseAsync(string suffix, CancellationToken ct)
    {
        var dbConnectionString = await fixture.CreateEmptyDatabaseAsync(
            $"plat03a_{suffix}_{Guid.NewGuid():N}",
            ct);

        await using var context = fixture.CreateDbContext(dbConnectionString);
        await context.Database.MigrateAsync(ct);
        return dbConnectionString;
    }

    private async Task<TenantDetails> RegisterSyntheticTenantAsync(
        string dbConnectionString,
        string publicId,
        string name,
        int maxActiveAccounts,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        await using var context = fixture.CreateDbContext(dbConnectionString);
        var handler = new RegisterTenantHandler(
            context,
            timeProvider,
            NullLogger<RegisterTenantHandler>.Instance);

        var result = await handler.HandleAsync(
            new RegisterTenantCommand(
                publicId,
                name,
                $"contacto@{publicId}.test",
                "Dr. Admin Inicial",
                $"admin.inicial@{publicId}.test",
                maxActiveAccounts),
            ct);

        Assert.Equal(RegisterTenantStatus.Created, result.Status);
        return result.Tenant!;
    }

    private static async Task SetOperationalStateForTestAsync(
        string dbConnectionString,
        Guid tenantId,
        DomainState state,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(dbConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE platform.tenants
            SET operational_state = @state
            WHERE id = @id;
            """,
            conn);
        cmd.Parameters.AddWithValue("state", state.ToCode());
        cmd.Parameters.AddWithValue("id", tenantId);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
    }

    private static async Task<RunningGrpcApiHost> StartGrpcApiHostAsync(
        string dbConnectionString,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var (httpPort, grpcPort) = AllocateTwoDistinctPorts();

        var app = PlatformHostFactory.CreateApiApplication(
            [
                $"--ConnectionStrings:PlatformDatabase={dbConnectionString}",
                $"--Ports:Http={httpPort}",
                "--InternalGrpc:Enabled=true",
                $"--InternalGrpc:Port={grpcPort}",
                $"--InternalGrpc:InternalServiceKey={SyntheticTestServiceKey}"
            ],
            builder =>
            {
                builder.Services.Replace(ServiceDescriptor.Singleton(timeProvider));
            });

        await app.StartAsync(ct);
        var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}");
        return new RunningGrpcApiHost(app, channel, httpPort, grpcPort);
    }

    private static (int HttpPort, int GrpcPort) AllocateTwoDistinctPorts()
    {
        using var httpListener = new TcpListener(IPAddress.Loopback, 0);
        httpListener.Start();
        var httpPort = ((IPEndPoint)httpListener.LocalEndpoint).Port;

        using var grpcListener = new TcpListener(IPAddress.Loopback, 0);
        grpcListener.Start();
        var grpcPort = ((IPEndPoint)grpcListener.LocalEndpoint).Port;

        return (httpPort, grpcPort);
    }

    private sealed class RunningGrpcApiHost(
        WebApplication app,
        GrpcChannel channel,
        int httpPort,
        int grpcPort) : IAsyncDisposable
    {
        private readonly CallInvoker _callInvoker = channel.CreateCallInvoker();

        public int HttpPort { get; } = httpPort;

        public int GrpcPort { get; } = grpcPort;

        public async Task<GetTenantAccessStateResponse> CallAsync(
            GetTenantAccessStateRequest request,
            string? serviceKey,
            CancellationToken ct)
        {
            Metadata? headers = null;
            if (serviceKey is not null)
            {
                headers = new Metadata
                {
                    { InternalGrpcOptions.HeaderName, serviceKey }
                };
            }

            return await _callInvoker
                .AsyncUnaryCall(GetTenantAccessStateMethod, null, new CallOptions(headers, cancellationToken: ct), request)
                .ResponseAsync;
        }

        public async ValueTask DisposeAsync()
        {
            channel.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
