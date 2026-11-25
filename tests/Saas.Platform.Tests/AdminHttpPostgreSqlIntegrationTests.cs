using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Saas.Contracts.PlatformIdentity.V1;
using Saas.Platform.Api;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.AdminHttp;
using Saas.Platform.Api.Features.Tenants.GetTenantAccessState;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Xunit;
using static Saas.Platform.Tests.AdminApiSecurityTests;

namespace Saas.Platform.Tests;

public sealed partial class AdminHttpPostgreSqlIntegrationTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>, IDisposable
{
    private static readonly HashSet<string> AllowedTenantDetailsKeys =
    [
        "id", "publicId", "name", "contactEmail", "operationalState", "mayAuthenticate",
        "maxActiveAccounts", "createdAtUtc", "updatedAtUtc", "initialProvisioning"
    ];

    private static readonly HashSet<string> AllowedProvisioningKeys =
    [
        "operationId", "provisioningRequestId", "status", "initialAdminFullName", "initialAdminEmail",
        "attemptCount", "manualInterventionRequired", "lastErrorCode", "lastErrorSummary",
        "createdAtUtc", "updatedAtUtc", "lastAttemptAtUtc", "nextAttemptAtUtc", "completedAtUtc", "stages"
    ];

    private static readonly HashSet<string> AllowedStageKeys =
    [
        "infrastructureInstance", "serviceDatabases", "clinicalPreparation", "identityPreparation"
    ];

    private static readonly HashSet<string> AllowedListResponseKeys =
    [
        "items", "page", "pageSize", "totalCount", "totalPages"
    ];

    private static readonly HashSet<string> AllowedListItemKeys =
    [
        "id", "publicId", "name", "operationalState", "mayAuthenticate", "maxActiveAccounts",
        "provisioningStatus", "manualInterventionRequired", "createdAtUtc", "updatedAtUtc"
    ];

    private static readonly string[] ForbiddenInternalFields =
    [
        "leaseOwner", "leaseUntilUtc", "leaseVersion", "concurrencyStamp",
        "infrastructureResourceRef", "clinicalMigrationVersion", "initialAdminAccountId",
        "operationalStateCode", "statusCode"
    ];

    private readonly TestJwtTokenFactory _tokens = new();

    public void Dispose() => _tokens.Dispose();

    [Fact]
    public async Task PostTenants_ValidAndDuplicateAndConcurrent_PersistAtomicallyAndReturnOpenApiCompliantResponses()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConn = await CreateIsolatedMigratedDbAsync("post_tenants", ct);
        var subjectId = Guid.NewGuid();
        var jwt = _tokens.CreateSuperAdminToken(subjectId: subjectId);
        var identityHandler = CreateActiveSuperAdminStub(subjectId);

        await using var host = await AdminTestHost.StartAsync(dbConn, _tokens.PublicKeyPem, identityHandler, ct);

        // 1. Successful atomic registration
        var createPayload = """
            {
              "publicId": "  Clinica-Norte-01  ",
              "name": "  Clínica Norte Sintética  ",
              "contactEmail": "  CONTACTO@Clinica-Norte.Vital-Demo.Test  ",
              "initialAdminFullName": "  Dra. Sofía Navarro  ",
              "initialAdminEmail": "  ADMIN.Norte@Clinica-Norte.Vital-Demo.Test  ",
              "maxActiveAccounts": 15
            }
            """;

        var createResponse = await SendJsonAsync(host.Client, HttpMethod.Post, "/v1/tenants", createPayload, jwt, ct);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("application/json", createResponse.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(createResponse.Headers.Location);

        var rawCreateJson = await createResponse.Content.ReadAsStringAsync(ct);
        AssertNoForbiddenInternalFields(rawCreateJson);

        using var createdDoc = JsonDocument.Parse(rawCreateJson);
        AssertOpenApiTenantDetails(createdDoc.RootElement);
        var createdId = createdDoc.RootElement.GetProperty("id").GetGuid();
        Assert.Equal($"/v1/tenants/{createdId:D}", createResponse.Headers.Location!.ToString());
        Assert.Equal("clinica-norte-01", createdDoc.RootElement.GetProperty("publicId").GetString());
        Assert.Equal("Clínica Norte Sintética", createdDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal("contacto@clinica-norte.vital-demo.test", createdDoc.RootElement.GetProperty("contactEmail").GetString());
        Assert.Equal("PENDIENTE_ADMIN", createdDoc.RootElement.GetProperty("operationalState").GetString());
        Assert.False(createdDoc.RootElement.GetProperty("mayAuthenticate").GetBoolean());
        Assert.Equal(15, createdDoc.RootElement.GetProperty("maxActiveAccounts").GetInt32());

        var prov = createdDoc.RootElement.GetProperty("initialProvisioning");
        Assert.Equal("PENDING", prov.GetProperty("status").GetString());
        Assert.Equal(0, prov.GetProperty("attemptCount").GetInt32());
        Assert.False(prov.GetProperty("manualInterventionRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, prov.GetProperty("lastErrorCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, prov.GetProperty("lastErrorSummary").ValueKind);
        Assert.Equal("PENDING", prov.GetProperty("stages").GetProperty("infrastructureInstance").GetString());
        Assert.Equal("PENDING", prov.GetProperty("stages").GetProperty("serviceDatabases").GetString());
        Assert.Equal("PENDING", prov.GetProperty("stages").GetProperty("clinicalPreparation").GetString());
        Assert.Equal("PENDING", prov.GetProperty("stages").GetProperty("identityPreparation").GetString());

        // 2. Duplicate normalized slug -> 409 TENANT_PUBLIC_ID_CONFLICT with existingTenantId
        var dupResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/v1/tenants",
            """{"publicId":"CLINICA-NORTE-01","name":"Otra","contactEmail":"a@b.test","initialAdminFullName":"Admin B","initialAdminEmail":"b@b.test","maxActiveAccounts":5}""",
            jwt,
            ct);

        Assert.Equal(HttpStatusCode.Conflict, dupResponse.StatusCode);
        Assert.Equal("application/problem+json", dupResponse.Content.Headers.ContentType?.MediaType);
        using var dupDoc = JsonDocument.Parse(await dupResponse.Content.ReadAsStringAsync(ct));
        Assert.Equal("TENANT_PUBLIC_ID_CONFLICT", dupDoc.RootElement.GetProperty("code").GetString());
        Assert.Equal(createdId, dupDoc.RootElement.GetProperty("existingTenantId").GetGuid());

        // 3. Concurrent duplicate registrations -> 1 x 201, 1 x 409 with same existingTenantId, 1 row in PostgreSQL
        var raceSlug = $"clinica-race-{Guid.NewGuid():N}"[..28];
        using var gate = new SemaphoreSlim(0, 2);

        async Task<(HttpStatusCode Status, JsonDocument Body)> RaceRegisterAsync(int index)
        {
            await gate.WaitAsync(ct);
            var resp = await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                "/v1/tenants",
                $$"""{"publicId":"{{raceSlug}}","name":"Clínica Race {{index}}","contactEmail":"c{{index}}@race.test","initialAdminFullName":"Admin {{index}}","initialAdminEmail":"a{{index}}@race.test","maxActiveAccounts":8}""",
                jwt,
                ct);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return (resp.StatusCode, doc);
        }

        var t1 = RaceRegisterAsync(1);
        var t2 = RaceRegisterAsync(2);
        gate.Release(2);
        var raceResults = await Task.WhenAll(t1, t2);

        var createdRace = Assert.Single(raceResults, r => r.Status == HttpStatusCode.Created);
        var conflictRace = Assert.Single(raceResults, r => r.Status == HttpStatusCode.Conflict);
        var createdRaceId = createdRace.Body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("TENANT_PUBLIC_ID_CONFLICT", conflictRace.Body.RootElement.GetProperty("code").GetString());
        Assert.Equal(createdRaceId, conflictRace.Body.RootElement.GetProperty("existingTenantId").GetGuid());
        createdRace.Body.Dispose();
        conflictRace.Body.Dispose();

        await using var verifyCtx = fixture.CreateDbContext(dbConn);
        Assert.Equal(1, await verifyCtx.Tenants.CountAsync(t => t.PublicId == raceSlug, ct));
        Assert.Equal(1, await verifyCtx.TenantProvisioningOperations.CountAsync(o => o.TenantId == createdRaceId, ct));
    }

    [Fact]
    public async Task PostTenants_InvalidDomainFieldsAndInvalidJsonPayloads_Return400WithoutCallingDbOrPersisting()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConn = await CreateIsolatedMigratedDbAsync("bad_inputs", ct);
        var subjectId = Guid.NewGuid();
        var jwt = _tokens.CreateSuperAdminToken(subjectId: subjectId);
        var identityHandler = CreateActiveSuperAdminStub(subjectId);

        await using var host = await AdminTestHost.StartAsync(dbConn, _tokens.PublicKeyPem, identityHandler, ct);

        // 1. Domain validation errors -> 400 VALIDATION_ERROR with camelCase field names
        var badDomainResp = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/v1/tenants",
            """{"publicId":"X","name":"","contactEmail":"bad-email","initialAdminFullName":"A","initialAdminEmail":"bad-admin","maxActiveAccounts":0}""",
            jwt,
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, badDomainResp.StatusCode);
        Assert.Equal("application/problem+json", badDomainResp.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await badDomainResp.Content.ReadAsStringAsync(ct)))
        {
            Assert.Equal("VALIDATION_ERROR", doc.RootElement.GetProperty("code").GetString());
            var fields = doc.RootElement.GetProperty("errors").EnumerateArray()
                .Select(e => e.GetProperty("field").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("publicId", fields);
            Assert.Contains("name", fields);
            Assert.Contains("contactEmail", fields);
            Assert.Contains("initialAdminFullName", fields);
            Assert.Contains("initialAdminEmail", fields);
            Assert.Contains("maxActiveAccounts", fields);
        }

        // 2. Malformed JSON, unknown fields, incompatible numeric types, null literal -> 400 INVALID_REQUEST_BODY
        var invalidBodies = new[]
        {
            """{"publicId":""",
            """null""",
            """{"publicId":"clinica-x","name":"X","contactEmail":"c@x.test","initialAdminFullName":"Admin X","initialAdminEmail":"a@x.test","maxActiveAccounts":10,"extraField":true}""",
            """{"publicId":"clinica-x","name":"X","contactEmail":"c@x.test","initialAdminFullName":"Admin X","initialAdminEmail":"a@x.test","maxActiveAccounts":"15"}""",
            """{"publicId":"clinica-x","name":"X","contactEmail":"c@x.test","initialAdminFullName":"Admin X","initialAdminEmail":"a@x.test","maxActiveAccounts":15.5}"""
        };

        foreach (var badJson in invalidBodies)
        {
            var resp = await SendJsonAsync(host.Client, HttpMethod.Post, "/v1/tenants", badJson, jwt, ct);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Assert.Equal("INVALID_REQUEST_BODY", doc.RootElement.GetProperty("code").GetString());
        }

        await using var verifyCtx = fixture.CreateDbContext(dbConn);
        Assert.Equal(0, await verifyCtx.Tenants.CountAsync(ct));
    }

    [Fact]
    public async Task RegisterTenantHandler_NonSlugUniqueViolation_IsClassifiedAsInternalConflictNotDuplicateSlug()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConn = await CreateIsolatedMigratedDbAsync("internal_conflict", ct);

        await using var ctx = fixture.CreateDbContext(dbConn);
        var handler = new RegisterTenantHandler(ctx, TimeProvider.System, NullLogger<RegisterTenantHandler>.Instance);

        var first = await handler.HandleAsync(
            new RegisterTenantCommand("clinica-base-int", "Clínica Base", "c@base.test", "Admin Base", "a@base.test", 10),
            ct);
        Assert.Equal(RegisterTenantStatus.Created, first.Status);
        var existingId = first.Tenant!.Id;

        var dbUpdateEx = new DbUpdateException(
            "Simulated non-slug unique violation",
            new PostgresException("duplicate", "ERROR", "ERROR", "23505", constraintName: "ux_tenant_provisioning_operations_tenant_id"));

        var isMapped = typeof(RegisterTenantHandler)
            .GetMethod("TryMapConflict", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var args = new object?[] { dbUpdateEx, "clinica-base-int", null, null };
        var matched = (bool)isMapped.Invoke(null, args)!;

        Assert.True(matched);
        Assert.Equal(RegisterTenantConflictKind.InternalConflict, (RegisterTenantConflictKind)args[2]!);
        Assert.NotEqual(Guid.Empty, existingId);
    }

    [Fact]
    public async Task GetTenantsListAndDetails_SupportsEmptyPaginationTieBreaksLegacyRowsExtremePagesAndControlledErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConn = await fixture.CreateEmptyDatabaseAsync($"plat04b_read_{Guid.NewGuid():N}", ct);

        var t0 = new DateTimeOffset(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        var legacyTenantId = Guid.CreateVersion7(t0);

        // Create pre-PLAT-02A legacy tenant without provisioning operation or quota
        await using (var plat01Ctx = fixture.CreateDbContext(dbConn))
        {
            await plat01Ctx.GetService<IMigrator>().MigrateAsync("20261122180000_InitialTenantCatalog", ct);
            await using var sqlConn = new NpgsqlConnection(dbConn);
            await sqlConn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO platform.tenants (id, public_id, name, contact_email, created_at_utc, updated_at_utc, concurrency_stamp)
                VALUES (@id, 'clinica-legado-p01', 'Clínica Legado P01', 'legado@vital-demo.test', @ts, @ts, @stamp);
                """,
                sqlConn);
            cmd.Parameters.AddWithValue("id", legacyTenantId);
            cmd.Parameters.AddWithValue("ts", t0);
            cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var migratedCtx = fixture.CreateDbContext(dbConn))
        {
            await migratedCtx.Database.MigrateAsync(ct);
        }

        var jwt = _tokens.CreateSuperAdminToken();
        var identityStub = new StubIdentityHttpHandler(_ => throw new InvalidOperationException("GET must not call Identity."));
        await using var host = await AdminTestHost.StartAsync(dbConn, _tokens.PublicKeyPem, identityStub, ct);

        // Test invalid pagination bounds -> 400 INVALID_PAGINATION
        foreach (var badQuery in new[]
        {
            "/v1/tenants?page=0",
            "/v1/tenants?page=-1",
            "/v1/tenants?page=abc",
            "/v1/tenants?page=99999999999",
            "/v1/tenants?pageSize=0",
            "/v1/tenants?pageSize=51",
            "/v1/tenants?page=1&page=2"
        })
        {
            var badResp = await SendAuthorizedGetAsync(host.Client, badQuery, jwt, ct);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
            using var badDoc = JsonDocument.Parse(await badResp.Content.ReadAsStringAsync(ct));
            Assert.Equal("INVALID_PAGINATION", badDoc.RootElement.GetProperty("code").GetString());
        }

        // Seed two newer tenants sharing identical CreatedAtUtc (t2) + one intermediate (t1) to test deterministic CreatedAtUtc DESC, Id DESC ordering
        var reg1 = await SeedTenantAsync(dbConn, "clinica-alpha", "Clínica Alpha", t1, maxActiveAccounts: 10, ct);
        var reg2a = await SeedTenantAsync(dbConn, "clinica-tie-a", "Clínica Tie A", t2, maxActiveAccounts: 20, ct);
        var reg2b = await SeedTenantAsync(dbConn, "clinica-tie-b", "Clínica Tie B", t2, maxActiveAccounts: 30, ct);

        // Simulate stage progress & internal raw error summary containing sensitive pattern on reg2b to confirm HTTP never leaks raw summary
        await using (var updateCtx = fixture.CreateDbContext(dbConn))
        {
            var op = await updateCtx.TenantProvisioningOperations.SingleAsync(o => o.TenantId == reg2b.Id, ct);
            op.RecordTerminalFailure(
                "IDENTITY_PROVISIONING_CONFLICT",
                "Internal provider detail Host=db.internal Password=SecretDoNotExpose!",
                t2.AddMinutes(5));

            await updateCtx.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE platform.tenant_provisioning_operations
                SET infrastructure_stage_state = 'CONFIRMED',
                    service_databases_stage_state = 'CONFIRMED',
                    clinical_stage_state = 'CONFIRMED',
                    identity_stage_state = 'VERIFICATION_REQUIRED',
                    lease_version = 4
                WHERE tenant_id = {reg2b.Id};
                """,
                ct);

            await updateCtx.SaveChangesAsync(ct);
        }

        // Page 1, size 2 -> expected top 2 ordered by CreatedAtUtc DESC, Id DESC
        var expectedTopIds = new[] { reg2a, reg2b }
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToArray();

        var page1Resp = await SendAuthorizedGetAsync(host.Client, "/v1/tenants?page=1&pageSize=2", jwt, ct);
        Assert.Equal(HttpStatusCode.OK, page1Resp.StatusCode);
        var rawPage1 = await page1Resp.Content.ReadAsStringAsync(ct);
        AssertNoForbiddenInternalFields(rawPage1);

        using (var page1Doc = JsonDocument.Parse(rawPage1))
        {
            AssertOpenApiListResponse(page1Doc.RootElement);
            Assert.Equal(1, page1Doc.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(2, page1Doc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(4, page1Doc.RootElement.GetProperty("totalCount").GetInt64());
            Assert.Equal(2, page1Doc.RootElement.GetProperty("totalPages").GetInt32());

            var items = page1Doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal(expectedTopIds[0], items[0].GetProperty("id").GetGuid());
            Assert.Equal(expectedTopIds[1], items[1].GetProperty("id").GetGuid());
        }

        // Page 2, size 2 -> reg1 then legacyTenantId (with explicit nulls)
        var page2Resp = await SendAuthorizedGetAsync(host.Client, "/v1/tenants?page=2&pageSize=2", jwt, ct);
        Assert.Equal(HttpStatusCode.OK, page2Resp.StatusCode);
        using (var page2Doc = JsonDocument.Parse(await page2Resp.Content.ReadAsStringAsync(ct)))
        {
            AssertOpenApiListResponse(page2Doc.RootElement);
            var items = page2Doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal(reg1.Id, items[0].GetProperty("id").GetGuid());
            Assert.Equal(legacyTenantId, items[1].GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, items[1].GetProperty("maxActiveAccounts").ValueKind);
            Assert.Equal(JsonValueKind.Null, items[1].GetProperty("provisioningStatus").ValueKind);
            Assert.False(items[1].GetProperty("manualInterventionRequired").GetBoolean());
        }

        // Extreme valid int32 page -> 200 OK empty items without OFFSET integer overflow
        var extremeResp = await SendAuthorizedGetAsync(host.Client, "/v1/tenants?page=2147483647&pageSize=50", jwt, ct);
        Assert.Equal(HttpStatusCode.OK, extremeResp.StatusCode);
        using (var extremeDoc = JsonDocument.Parse(await extremeResp.Content.ReadAsStringAsync(ct)))
        {
            AssertOpenApiListResponse(extremeDoc.RootElement);
            Assert.Empty(extremeDoc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(4, extremeDoc.RootElement.GetProperty("totalCount").GetInt64());
        }

        // Detail endpoint: reg2b stages + controlled summary (must NOT contain raw internal diagnostics)
        var detailResp = await SendAuthorizedGetAsync(host.Client, $"/v1/tenants/{reg2b.Id:D}", jwt, ct);
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var rawDetail = await detailResp.Content.ReadAsStringAsync(ct);
        AssertNoForbiddenInternalFields(rawDetail);
        Assert.DoesNotContain("SecretDoNotExpose", rawDetail);
        Assert.DoesNotContain("Internal provider detail", rawDetail);

        using (var detailDoc = JsonDocument.Parse(rawDetail))
        {
            AssertOpenApiTenantDetails(detailDoc.RootElement);
            var prov = detailDoc.RootElement.GetProperty("initialProvisioning");
            Assert.Equal("FAILED_TERMINAL", prov.GetProperty("status").GetString());
            Assert.True(prov.GetProperty("manualInterventionRequired").GetBoolean());
            Assert.Equal("IDENTITY_PROVISIONING_CONFLICT", prov.GetProperty("lastErrorCode").GetString());
            Assert.Equal(
                "Identity reported a payload conflict for the provisioning request and requires administrative review.",
                prov.GetProperty("lastErrorSummary").GetString());
            Assert.Equal("CONFIRMED", prov.GetProperty("stages").GetProperty("infrastructureInstance").GetString());
            Assert.Equal("CONFIRMED", prov.GetProperty("stages").GetProperty("serviceDatabases").GetString());
            Assert.Equal("CONFIRMED", prov.GetProperty("stages").GetProperty("clinicalPreparation").GetString());
            Assert.Equal("VERIFICATION_REQUIRED", prov.GetProperty("stages").GetProperty("identityPreparation").GetString());
        }

        // Detail endpoint: legacy row preserves null quota and null initialProvisioning
        var legacyDetailResp = await SendAuthorizedGetAsync(host.Client, $"/v1/tenants/{legacyTenantId:D}", jwt, ct);
        Assert.Equal(HttpStatusCode.OK, legacyDetailResp.StatusCode);
        using (var legacyDoc = JsonDocument.Parse(await legacyDetailResp.Content.ReadAsStringAsync(ct)))
        {
            AssertOpenApiTenantDetails(legacyDoc.RootElement);
            Assert.Equal(JsonValueKind.Null, legacyDoc.RootElement.GetProperty("maxActiveAccounts").ValueKind);
            Assert.Equal(JsonValueKind.Null, legacyDoc.RootElement.GetProperty("initialProvisioning").ValueKind);
        }

        // Detail endpoint: invalid tenantId -> 400 INVALID_TENANT_ID; nonexistent -> 404 TENANT_NOT_FOUND
        foreach (var invalidId in new[] { "not-a-uuid", "00000000-0000-0000-0000-000000000000" })
        {
            var invResp = await SendAuthorizedGetAsync(host.Client, $"/v1/tenants/{invalidId}", jwt, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invResp.StatusCode);
            using var invDoc = JsonDocument.Parse(await invResp.Content.ReadAsStringAsync(ct));
            Assert.Equal("INVALID_TENANT_ID", invDoc.RootElement.GetProperty("code").GetString());
        }

        var missingResp = await SendAuthorizedGetAsync(host.Client, $"/v1/tenants/{Guid.NewGuid():D}", jwt, ct);
        Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);
        using (var missDoc = JsonDocument.Parse(await missingResp.Content.ReadAsStringAsync(ct)))
        {
            Assert.Equal("TENANT_NOT_FOUND", missDoc.RootElement.GetProperty("code").GetString());
        }

        // Empty list test on fresh DB
        var emptyDbConn = await CreateIsolatedMigratedDbAsync("empty_list", ct);
        await using var emptyHost = await AdminTestHost.StartAsync(emptyDbConn, _tokens.PublicKeyPem, identityStub, ct);
        var emptyResp = await SendAuthorizedGetAsync(emptyHost.Client, "/v1/tenants", jwt, ct);
        Assert.Equal(HttpStatusCode.OK, emptyResp.StatusCode);
        using var emptyDoc = JsonDocument.Parse(await emptyResp.Content.ReadAsStringAsync(ct));
        AssertOpenApiListResponse(emptyDoc.RootElement);
        Assert.Empty(emptyDoc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, emptyDoc.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, emptyDoc.RootElement.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    public async Task PostgreSqlUnavailable_Returns503ServiceUnavailableAcrossAllAdminEndpoints_WithoutLeakingSqlDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        int closedPort;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var brokenDb = $"Host=127.0.0.1;Port={closedPort};Database=unavailable_db;Username=postgres;Timeout=1;Command Timeout=1";
        var subjectId = Guid.NewGuid();
        var jwt = _tokens.CreateSuperAdminToken(subjectId: subjectId);
        var identityHandler = CreateActiveSuperAdminStub(subjectId);

        await using var host = await AdminTestHost.StartAsync(brokenDb, _tokens.PublicKeyPem, identityHandler, ct);

        var responses = new[]
        {
            await SendAuthorizedGetAsync(host.Client, "/v1/tenants", jwt, ct),
            await SendAuthorizedGetAsync(host.Client, $"/v1/tenants/{Guid.NewGuid():D}", jwt, ct),
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                "/v1/tenants",
                """{"publicId":"clinica-down","name":"Clínica Down","contactEmail":"c@d.test","initialAdminFullName":"Admin D","initialAdminEmail":"a@d.test","maxActiveAccounts":5}""",
                jwt,
                ct)
        };

        foreach (var resp in responses)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
            var text = await resp.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("Host=127.0.0.1", text);
            Assert.DoesNotContain("Npgsql", text);
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("SERVICE_UNAVAILABLE", doc.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task AdminApiWithInternalGrpcAndWorkerHost_PreserveHealthChecksGrpcAccessAndWorkerSeparation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConn = await CreateIsolatedMigratedDbAsync("combined_regression", ct);
        const string grpcKey = "SyntheticPlat04BCombinedGrpcKey!2026";
        var subjectId = Guid.NewGuid();
        var jwt = _tokens.CreateSuperAdminToken(subjectId: subjectId);

        int httpPort;
        int grpcPort;
        using (var l1 = new TcpListener(IPAddress.Loopback, 0))
        using (var l2 = new TcpListener(IPAddress.Loopback, 0))
        {
            l1.Start();
            l2.Start();
            httpPort = ((IPEndPoint)l1.LocalEndpoint).Port;
            grpcPort = ((IPEndPoint)l2.LocalEndpoint).Port;
        }

        var identityStub = CreateActiveSuperAdminStub(subjectId);
        var app = PlatformHostFactory.CreateApiApplication(
            [
                $"--ConnectionStrings:PlatformDatabase={dbConn}",
                $"--Ports:Http={httpPort}",
                "--InternalGrpc:Enabled=true",
                $"--InternalGrpc:Port={grpcPort}",
                $"--InternalGrpc:InternalServiceKey={grpcKey}",
                "--AdminApi:Enabled=true",
                $"--Jwt:PublicKeyPem={_tokens.PublicKeyPem}",
                "--IdentityAuth:BaseAddress=http://identity.test.local"
            ],
            builder =>
            {
                builder.Services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                    new CombinedReplaceHandlerFilter(identityStub));
            });

        await app.StartAsync(ct);
        try
        {
            // Worker service stays strictly separated from API host
            Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is ProvisioningWorkerHostedService);

            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
            Assert.Equal(HttpStatusCode.OK, (await httpClient.GetAsync("/health/live", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await httpClient.GetAsync("/health/ready", ct)).StatusCode);

            // Create via HTTP, then query via both gRPC and HTTP list
            var createdResp = await SendJsonAsync(
                httpClient,
                HttpMethod.Post,
                "/v1/tenants",
                """{"publicId":"clinica-combo-01","name":"Clínica Combo","contactEmail":"c@combo.test","initialAdminFullName":"Admin Combo","initialAdminEmail":"a@combo.test","maxActiveAccounts":12}""",
                jwt,
                ct);
            Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);

            using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}");
            var invoker = channel.CreateCallInvoker();
            var grpcMethod = new Method<GetTenantAccessStateRequest, GetTenantAccessStateResponse>(
                MethodType.Unary,
                "vital.platform_identity.v1.TenantAccessService",
                "GetTenantAccessState",
                Marshallers.Create(r => r.ToByteArray(), GetTenantAccessStateRequest.Parser.ParseFrom),
                Marshallers.Create(r => r.ToByteArray(), GetTenantAccessStateResponse.Parser.ParseFrom));

            var grpcResp = await invoker.AsyncUnaryCall(
                grpcMethod,
                null,
                new CallOptions(new Metadata { { InternalGrpcOptions.HeaderName, grpcKey } }, cancellationToken: ct),
                new GetTenantAccessStateRequest { Slug = "clinica-combo-01" }).ResponseAsync;

            Assert.Equal("clinica-combo-01", grpcResp.Slug);
            Assert.Equal(12, grpcResp.MaxActiveAccounts);
            Assert.False(grpcResp.MayAuthenticate);

            // Worker host mode resolves cleanly without HTTP server
            Assert.Equal(PlatformRuntimeMode.Worker, PlatformHostFactory.ResolveRuntimeMode(["--mode=worker"]));
            using var workerHost = PlatformHostFactory.CreateWorkerHost([$"--ConnectionStrings:PlatformDatabase={dbConn}"]);
            Assert.Contains(workerHost.Services.GetServices<IHostedService>(), s => s is ProvisioningWorkerHostedService);
        }
        finally
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
        }
    }

    private async Task<string> CreateIsolatedMigratedDbAsync(string suffix, CancellationToken ct)
    {
        var dbConn = await fixture.CreateEmptyDatabaseAsync($"plat04b_int_{suffix}_{Guid.NewGuid():N}", ct);
        await using var ctx = fixture.CreateDbContext(dbConn);
        await ctx.Database.MigrateAsync(ct);
        return dbConn;
    }

    private async Task<Api.Features.Tenants.GetTenantById.TenantDetails> SeedTenantAsync(
        string dbConn,
        string slug,
        string name,
        DateTimeOffset createdAtUtc,
        int maxActiveAccounts,
        CancellationToken ct)
    {
        await using var ctx = fixture.CreateDbContext(dbConn);
        var handler = new RegisterTenantHandler(ctx, new MutableTimeProvider(createdAtUtc), NullLogger<RegisterTenantHandler>.Instance);
        var res = await handler.HandleAsync(
            new RegisterTenantCommand(slug, name, $"contacto@{slug}.test", "Admin Seed", $"admin@{slug}.test", maxActiveAccounts),
            ct);
        Assert.Equal(RegisterTenantStatus.Created, res.Status);
        return res.Tenant!;
    }

    private static StubIdentityHttpHandler CreateActiveSuperAdminStub(Guid subjectId) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"id":"{{subjectId:D}}","tenantId":null,"active":true,"role":"SUPER_ADMIN"}""",
                Encoding.UTF8,
                "application/json")
        });

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string jsonBody,
        string jwt,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await client.SendAsync(req, ct);
    }

    private static async Task<HttpResponseMessage> SendAuthorizedGetAsync(
        HttpClient client,
        string path,
        string jwt,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await client.SendAsync(req, ct);
    }

    private static void AssertNoForbiddenInternalFields(string rawJson)
    {
        foreach (var forbidden in ForbiddenInternalFields)
        {
            Assert.DoesNotContain($"\"{forbidden}\"", rawJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertOpenApiTenantDetails(JsonElement element)
    {
        var keys = element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(AllowedTenantDetailsKeys, keys);
        Assert.Matches(UtcIso8601ZRegex(), element.GetProperty("createdAtUtc").GetString()!);
        Assert.Matches(UtcIso8601ZRegex(), element.GetProperty("updatedAtUtc").GetString()!);

        var opState = element.GetProperty("operationalState").GetString();
        Assert.Contains(opState, new[] { "PENDIENTE_ADMIN", "ACTIVA", "SUSPENDIDA" });

        var prov = element.GetProperty("initialProvisioning");
        if (prov.ValueKind != JsonValueKind.Null)
        {
            var provKeys = prov.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(AllowedProvisioningKeys, provKeys);
            Assert.Contains(prov.GetProperty("status").GetString(), new[] { "PENDING", "IN_PROGRESS", "FAILED_RETRYABLE", "FAILED_TERMINAL", "COMPLETED" });
            Assert.Matches(UtcIso8601ZRegex(), prov.GetProperty("createdAtUtc").GetString()!);
            Assert.Matches(UtcIso8601ZRegex(), prov.GetProperty("updatedAtUtc").GetString()!);

            var stages = prov.GetProperty("stages");
            var stageKeys = stages.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(AllowedStageKeys, stageKeys);
        }
    }

    private static void AssertOpenApiListResponse(JsonElement element)
    {
        var keys = element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(AllowedListResponseKeys, keys);

        foreach (var item in element.GetProperty("items").EnumerateArray())
        {
            var itemKeys = item.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(AllowedListItemKeys, itemKeys);
            Assert.Matches(UtcIso8601ZRegex(), item.GetProperty("createdAtUtc").GetString()!);
            Assert.Matches(UtcIso8601ZRegex(), item.GetProperty("updatedAtUtc").GetString()!);
        }
    }

    private sealed class CombinedReplaceHandlerFilter(HttpMessageHandler replacementHandler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (string.Equals(builder.Name, nameof(IIdentityAccountVerifier), StringComparison.Ordinal))
            {
                builder.PrimaryHandler = replacementHandler;
            }
        };
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcIso8601ZRegex();
}
