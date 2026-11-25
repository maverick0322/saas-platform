using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Npgsql;
using Saas.Platform.Api;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Tenants.AdminHttp;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class AdminApiSecurityTests(PostgreSqlFixture fixture)
    : IClassFixture<PostgreSqlFixture>, IDisposable
{
    private readonly TestJwtTokenFactory _tokens = new();

    public void Dispose() => _tokens.Dispose();

    [Fact]
    public async Task StartupValidation_InvalidAdminSecurityConfiguration_FailsFast()
    {
        var db = fixture.ConnectionString;

        // 1. Missing both PublicKeyPem and PublicKeyPemPath
        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            "--IdentityAuth:BaseAddress=http://127.0.0.1:8080"
        ]));

        // 2. Both PublicKeyPem and PublicKeyPemPath provided
        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            $"--Jwt:PublicKeyPem={_tokens.PublicKeyPem}",
            "--Jwt:PublicKeyPemPath=/tmp/nonexistent.pem",
            "--IdentityAuth:BaseAddress=http://127.0.0.1:8080"
        ]));

        // 3. Invalid PEM text
        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            "--Jwt:PublicKeyPem=NOT_A_VALID_PEM",
            "--IdentityAuth:BaseAddress=http://127.0.0.1:8080"
        ]));

        // 4. RSA key smaller than 2048 bits
        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            $"--Jwt:PublicKeyPem={TestJwtTokenFactory.CreateSmallRsa1024PublicKeyPem()}",
            "--IdentityAuth:BaseAddress=http://127.0.0.1:8080"
        ]));

        // 5. Private key passed into PublicKeyPem
        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            $"--Jwt:PublicKeyPem={_tokens.PrivateKeyPem}",
            "--IdentityAuth:BaseAddress=http://127.0.0.1:8080"
        ]));

        // 6. Forbidden secret configuration keys present
        foreach (var forbiddenKey in new[]
        {
            "Jwt:PrivateKeyPem",
            "Jwt:PrivateKeyPemPath",
            "Jwt:RefreshTokenHmacKey",
            "Jwt:SigningKey"
        })
        {
            Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
                $"--ConnectionStrings:PlatformDatabase={db}",
                "--AdminApi:Enabled=true",
                $"--Jwt:PublicKeyPem={_tokens.PublicKeyPem}",
                $"--{forbiddenKey}=forbidden-secret-value",
                "--IdentityAuth:BaseAddress=http://127.0.0.1:8080"
            ]));
        }

        // 7. Missing or invalid IdentityAuth:BaseAddress or TimeoutSeconds
        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            $"--Jwt:PublicKeyPem={_tokens.PublicKeyPem}"
        ]));

        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            $"--Jwt:PublicKeyPem={_tokens.PublicKeyPem}",
            "--IdentityAuth:BaseAddress=ftp://identity.internal",
            "--IdentityAuth:TimeoutSeconds=3"
        ]));

        Assert.Throws<InvalidOperationException>(() => PlatformHostFactory.CreateApiApplication([
            $"--ConnectionStrings:PlatformDatabase={db}",
            "--AdminApi:Enabled=true",
            $"--Jwt:PublicKeyPem={_tokens.PublicKeyPem}",
            "--IdentityAuth:BaseAddress=http://identity.internal",
            "--IdentityAuth:TimeoutSeconds=0"
        ]));

        // 8. Mounted PEM file via Jwt:PublicKeyPemPath succeeds cleanly
        var tempPemPath = Path.Combine(Path.GetTempPath(), $"plat04b-pub-{Guid.NewGuid():N}.pem");
        try
        {
            await File.WriteAllTextAsync(tempPemPath, _tokens.PublicKeyPem, TestContext.Current.CancellationToken);
            var httpPort = AllocateFreePort();
            await using var app = PlatformHostFactory.CreateApiApplication([
                $"--ConnectionStrings:PlatformDatabase={db}",
                $"--Urls=http://127.0.0.1:{httpPort}",
                "--AdminApi:Enabled=true",
                $"--Jwt:PublicKeyPemPath={tempPemPath}",
                "--IdentityAuth:BaseAddress=http://127.0.0.1:59999"
            ]);
            await app.StartAsync(TestContext.Current.CancellationToken);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/tenants");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.CreateSuperAdminToken());
            var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            await app.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (File.Exists(tempPemPath))
            {
                File.Delete(tempPemPath);
            }
        }
    }

    [Fact]
    public async Task JwtAuthentication_RejectsInvalidTokensWith401AndWwwAuthenticate_WithoutCallingIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var stubIdentity = new StubIdentityHttpHandler(_ => throw new InvalidOperationException("Identity must not be called on GET or failed JWT."));

        await using var host = await AdminTestHost.StartAsync(fixture.ConnectionString, _tokens.PublicKeyPem, stubIdentity, ct);

        var sub = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        long exp = now.AddMinutes(15).ToUnixTimeSeconds();
        long nbf = now.ToUnixTimeSeconds();

        var unauthorizedTokens = new (string Label, string? HeaderValue)[]
        {
            ("missing header", null),
            ("basic scheme", "Basic dXNlcjpwYXNz"),
            ("malformed jwt", "Bearer not.a.jwt"),
            ("expired beyond clock skew", "Bearer " + _tokens.CreateSuperAdminToken(lifetime: TimeSpan.FromSeconds(-45), nowUtc: now.AddMinutes(-1))),
            ("wrong signing key", "Bearer " + _tokens.CreateRawPayloadRs256Token(JsonSerializer.Serialize(new { sub, iss = "saas-identity", aud = "vital-saas", role = "SUPER_ADMIN", nbf, exp }))),
            ("wrong issuer", "Bearer " + _tokens.CreateSuperAdminToken(issuer: "other-issuer")),
            ("wrong audience", "Bearer " + _tokens.CreateSuperAdminToken(audience: "other-audience")),
            ("HS256 rejected", "Bearer " + _tokens.CreateHs256Token()),
            ("alg none rejected", "Bearer " + _tokens.CreateAlgNoneToken()),
            ("SUPER_ADMIN with tenant_id uuid", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","tenant_id":"{{Guid.NewGuid():D}}","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("SUPER_ADMIN with empty tenant_id", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","tenant_id":"","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("SUPER_ADMIN with null tenant_id", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","tenant_id":null,"nbf":{{nbf}},"exp":{{exp}}}""")),
            ("missing sub", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("empty guid sub", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"00000000-0000-0000-0000-000000000000","iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("non-guid sub", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"admin-1","iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("duplicate sub keys", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","sub":"{{Guid.NewGuid():D}}","iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("unknown role", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","iss":"saas-identity","aud":"vital-saas","role":"UNKNOWN_ROLE","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("multiple role array", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","iss":"saas-identity","aud":"vital-saas","role":["SUPER_ADMIN","ADMIN_CLINICA"],"nbf":{{nbf}},"exp":{{exp}}}""")),
            ("duplicate role keys", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","iss":"saas-identity","aud":"vital-saas","role":"SUPER_ADMIN","role":"ADMIN_CLINICA","nbf":{{nbf}},"exp":{{exp}}}""")),
            ("clinic role without tenant_id", "Bearer " + _tokens.CreateClinicUserToken("ADMIN_CLINICA", Guid.NewGuid(), includeTenantId: false)),
            ("clinic role with empty tenant_id", "Bearer " + _tokens.CreateRawPayloadRs256Token($$"""{"sub":"{{sub}}","iss":"saas-identity","aud":"vital-saas","role":"ADMIN_CLINICA","tenant_id":"00000000-0000-0000-0000-000000000000","nbf":{{nbf}},"exp":{{exp}}}"""))
        };

        // Also test forged signing key case with explicit wrong key flag
        unauthorizedTokens[4] = (
            "wrong signing key",
            "Bearer " + _tokens.CreateRawPayloadRs256Token(
                JsonSerializer.Serialize(new { sub, iss = "saas-identity", aud = "vital-saas", role = "SUPER_ADMIN", nbf, exp }),
                useWrongSigningKey: true));

        foreach (var (label, headerValue) in unauthorizedTokens)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/tenants");
            if (headerValue is not null)
            {
                req.Headers.TryAddWithoutValidation("Authorization", headerValue);
            }

            req.Headers.Add("X-Role", "SUPER_ADMIN");
            req.Headers.Add("X-Tenant-Id", string.Empty);

            var resp = await host.Client.SendAsync(req, ct);
            Assert.True(resp.StatusCode == HttpStatusCode.Unauthorized, $"Expected 401 for '{label}', got {resp.StatusCode}.");
            Assert.Contains("Bearer", resp.Headers.WwwAuthenticate.Select(h => h.Scheme));
            Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            Assert.Equal("UNAUTHORIZED", doc.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(0, stubIdentity.CallCount);
    }

    [Theory]
    [InlineData("ADMIN_CLINICA")]
    [InlineData("ESPECIALISTA")]
    [InlineData("AUXILIAR")]
    public async Task ClinicRoles_WithCoherentTokenAndForgedHeaders_Return403Forbidden_WithoutCallingIdentity(string role)
    {
        var ct = TestContext.Current.CancellationToken;
        var stubIdentity = new StubIdentityHttpHandler(_ => throw new InvalidOperationException("Identity must not be called for non-SUPER_ADMIN."));
        await using var host = await AdminTestHost.StartAsync(fixture.ConnectionString, _tokens.PublicKeyPem, stubIdentity, ct);

        var clinicToken = _tokens.CreateClinicUserToken(role, Guid.NewGuid());
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/tenants")
        {
            Content = new StringContent(
                """{"publicId":"clinica-forbidden","name":"Clínica Forbidden","contactEmail":"a@b.test","initialAdminFullName":"Admin F","initialAdminEmail":"b@b.test","maxActiveAccounts":5}""",
                Encoding.UTF8,
                "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clinicToken);
        req.Headers.Add("X-Role", "SUPER_ADMIN");
        req.Headers.Add("X-User-Id", Guid.NewGuid().ToString("D"));

        var resp = await host.Client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        Assert.Equal("FORBIDDEN", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, stubIdentity.CallCount);
    }

    [Fact]
    public async Task PostTenants_IdentityMeVerificationScenarios_EnforceFailClosedAndDoNotPersistOnRejection()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbConn = await CreateIsolatedMigratedDbAsync("me_verify", ct);
        var subjectId = Guid.NewGuid();
        var superAdminJwt = _tokens.CreateSuperAdminToken(subjectId: subjectId);

        Func<HttpRequestMessage, HttpResponseMessage> currentResponder = _ =>
            new HttpResponseMessage(HttpStatusCode.OK);

        var stubHandler = new StubIdentityHttpHandler(req => currentResponder(req));
        await using var host = await AdminTestHost.StartAsync(dbConn, _tokens.PublicKeyPem, stubHandler, ct, identityTimeoutSeconds: 1);

        async Task<HttpResponseMessage> PostAsync(string slug)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/tenants")
            {
                Content = new StringContent(
                    $$"""{"publicId":"{{slug}}","name":"Clínica Verify","contactEmail":"c@verify.test","initialAdminFullName":"Admin Verify","initialAdminEmail":"a@verify.test","maxActiveAccounts":10}""",
                    Encoding.UTF8,
                    "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superAdminJwt);
            return await host.Client.SendAsync(req, ct);
        }

        // 1. Account disabled (active: false) -> 403
        currentResponder = _ => JsonOk($$"""{"id":"{{subjectId:D}}","tenantId":null,"active":false,"role":"SUPER_ADMIN"}""");
        var respDisabled = await PostAsync("clinica-me-disabled");
        Assert.Equal(HttpStatusCode.Forbidden, respDisabled.StatusCode);

        // 2. Role changed to non-SUPER_ADMIN valid role -> 403
        currentResponder = _ => JsonOk($$"""{"id":"{{subjectId:D}}","tenantId":null,"active":true,"role":"ADMIN_CLINICA"}""");
        var respWrongRole = await PostAsync("clinica-me-role");
        Assert.Equal(HttpStatusCode.Forbidden, respWrongRole.StatusCode);

        // 3. Identity returns 401 -> 401 + WWW-Authenticate
        currentResponder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"detail":"internal secret trace"}""", Encoding.UTF8, "application/json")
        };
        var resp401 = await PostAsync("clinica-me-401");
        Assert.Equal(HttpStatusCode.Unauthorized, resp401.StatusCode);
        Assert.Contains("Bearer", resp401.Headers.WwwAuthenticate.Select(h => h.Scheme));
        var body401 = await resp401.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("internal secret trace", body401);

        // 4. Identity returns 403 -> 403 without forwarding internal body
        currentResponder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"detail":"raw identity reason"}""", Encoding.UTF8, "application/json")
        };
        var resp403 = await PostAsync("clinica-me-403");
        Assert.Equal(HttpStatusCode.Forbidden, resp403.StatusCode);
        Assert.DoesNotContain("raw identity reason", await resp403.Content.ReadAsStringAsync(ct));

        // 5. Incoherent / malformed / unavailable responses -> 503 SERVICE_UNAVAILABLE
        var unavailableResponders = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => JsonOk($$"""{"id":"{{Guid.NewGuid():D}}","tenantId":null,"active":true,"role":"SUPER_ADMIN"}"""), // different sub
            _ => JsonOk($$"""{"id":"{{subjectId:D}}","tenantId":"{{Guid.NewGuid():D}}","active":true,"role":"SUPER_ADMIN"}"""), // non-null tenantId
            _ => JsonOk($$"""{"id":"{{subjectId:D}}","active":true,"role":"SUPER_ADMIN"}"""), // missing tenantId property
            _ => JsonOk($$"""{"id":"{{subjectId:D}}","tenantId":null,"role":"SUPER_ADMIN"}"""), // missing active property
            _ => JsonOk($$"""{"id":"{{subjectId:D}}","tenantId":null,"active":true}"""), // missing role property
            _ => JsonOk("""{"not valid json"""), // invalid JSON
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("PG Exception details") },
            _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://127.0.0.1/redirected") } },
            _ => throw new HttpRequestException("Simulated connection refused")
        };

        foreach (var responder in unavailableResponders)
        {
            currentResponder = responder;
            var resp503 = await PostAsync("clinica-me-503");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp503.StatusCode);
            var text = await resp503.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("PG Exception details", text);
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("SERVICE_UNAVAILABLE", doc.RootElement.GetProperty("code").GetString());
        }

        // 6. Timeout (> 1s) -> 503
        var timeoutHandler = new SlowIdentityHttpHandler(TimeSpan.FromSeconds(3));
        await using (var slowHost = await AdminTestHost.StartAsync(dbConn, _tokens.PublicKeyPem, timeoutHandler, ct, identityTimeoutSeconds: 1))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/tenants")
            {
                Content = new StringContent(
                    """{"publicId":"clinica-me-timeout","name":"Clínica Timeout","contactEmail":"c@t.test","initialAdminFullName":"Admin T","initialAdminEmail":"a@t.test","maxActiveAccounts":5}""",
                    Encoding.UTF8,
                    "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superAdminJwt);
            var timeoutResp = await slowHost.Client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, timeoutResp.StatusCode);
        }

        // Confirm zero rows created after all rejections
        await using (var verifyCtx = fixture.CreateDbContext(dbConn))
        {
            Assert.Equal(0, await verifyCtx.Tenants.CountAsync(ct));
            Assert.Equal(0, await verifyCtx.TenantProvisioningOperations.CountAsync(ct));
        }

        // 7. Coherent active profile forwards the same Bearer token and succeeds (201) -> GETs do NOT call Identity
        var validCallsBefore = stubHandler.CallCount;
        currentResponder = req =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(superAdminJwt, req.Headers.Authorization?.Parameter);
            return JsonOk($$"""{"id":"{{subjectId:D}}","tenantId":null,"active":true,"role":"SUPER_ADMIN"}""");
        };

        var createdResp = await PostAsync("clinica-me-ok");
        Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);
        Assert.Equal(validCallsBefore + 1, stubHandler.CallCount);

        using var createdDoc = JsonDocument.Parse(await createdResp.Content.ReadAsStringAsync(ct));
        var createdId = createdDoc.RootElement.GetProperty("id").GetString();

        var callsAfterCreate = stubHandler.CallCount;
        using (var getListReq = new HttpRequestMessage(HttpMethod.Get, "/v1/tenants"))
        {
            getListReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superAdminJwt);
            var listResp = await host.Client.SendAsync(getListReq, ct);
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        }

        using (var getByIdReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/tenants/{createdId}"))
        {
            getByIdReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superAdminJwt);
            var detailResp = await host.Client.SendAsync(getByIdReq, ct);
            Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        }

        Assert.Equal(callsAfterCreate, stubHandler.CallCount);
    }

    [Fact]
    public void ControlledSummaryCatalogAndDatabaseClassifier_EnforceStrictBoundaries()
    {
        var summary = ProvisioningAdministrativeErrorCatalog.ResolveControlledSummary(
            "CUSTOM_UNKNOWN_CODE",
            ProvisioningOperationStatus.FailedTerminal,
            manualInterventionRequired: true);

        Assert.NotNull(summary);
        Assert.DoesNotContain("Host=", summary);
        Assert.Null(ProvisioningAdministrativeErrorCatalog.ResolveControlledSummary(
            null,
            ProvisioningOperationStatus.Pending,
            manualInterventionRequired: false));

        Assert.False(DatabaseExceptionClassifier.IsPostgreSqlUnavailable(new InvalidOperationException("boom")));
        Assert.False(DatabaseExceptionClassifier.IsPostgreSqlUnavailable(new PostgresException("dup", "ERROR", "ERROR", "23505")));
        Assert.True(DatabaseExceptionClassifier.IsPostgreSqlUnavailable(new PostgresException("conn", "FATAL", "FATAL", "08006")));
        Assert.True(DatabaseExceptionClassifier.IsPostgreSqlUnavailable(new TimeoutException("pg timeout")));
    }

    private async Task<string> CreateIsolatedMigratedDbAsync(string suffix, CancellationToken ct)
    {
        var dbConn = await fixture.CreateEmptyDatabaseAsync($"plat04b_sec_{suffix}_{Guid.NewGuid():N}", ct);
        await using var ctx = fixture.CreateDbContext(dbConn);
        await ctx.Database.MigrateAsync(ct);
        return dbConn;
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static int AllocateFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal sealed class AdminTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private AdminTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<AdminTestHost> StartAsync(
            string dbConnectionString,
            string publicKeyPem,
            HttpMessageHandler identityHandler,
            CancellationToken ct,
            int identityTimeoutSeconds = 3)
        {
            var httpPort = AllocateFreePort();
            var app = PlatformHostFactory.CreateApiApplication(
                [
                    $"--ConnectionStrings:PlatformDatabase={dbConnectionString}",
                    $"--Urls=http://127.0.0.1:{httpPort}",
                    "--AdminApi:Enabled=true",
                    $"--Jwt:PublicKeyPem={publicKeyPem}",
                    "--IdentityAuth:BaseAddress=http://identity.test.local",
                    $"--IdentityAuth:TimeoutSeconds={identityTimeoutSeconds}"
                ],
                builder =>
                {
                    builder.Services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                        new ReplaceIdentityPrimaryHandlerFilter(identityHandler));
                });

            await app.StartAsync(ct);
            var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
            return new AdminTestHost(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class ReplaceIdentityPrimaryHandlerFilter(HttpMessageHandler replacementHandler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (string.Equals(builder.Name, nameof(IIdentityAccountVerifier), StringComparison.Ordinal))
            {
                Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
                var socketsHandler = (SocketsHttpHandler)builder.PrimaryHandler;
                Assert.False(socketsHandler.AllowAutoRedirect);
                builder.PrimaryHandler = replacementHandler;
            }
        };
    }

    internal sealed class StubIdentityHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class SlowIdentityHttpHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return JsonOk("{}");
        }
    }
}
