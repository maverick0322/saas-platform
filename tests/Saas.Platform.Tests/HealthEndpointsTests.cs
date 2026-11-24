using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class HealthEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetHealthLive_ReturnsOkWithProcessLivenessPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/health/live", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.Equal("saas-platform", root.GetProperty("service").GetString());
        Assert.Equal("live", root.GetProperty("probe").GetString());
        Assert.Equal("process-only", root.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task GetHealthReady_ReturnsOkWithProcessOnlyReadinessPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.Equal("saas-platform", root.GetProperty("service").GetString());
        Assert.Equal("ready", root.GetProperty("probe").GetString());
        Assert.Equal("process-only", root.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task GetUnknownHealthRoute_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/health/unknown", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/v1/tenants")]
    [InlineData("POST", "/v1/tenants")]
    [InlineData("GET", "/v1/tenants/01935500-0000-7000-8000-000000000001")]
    [InlineData("GET", "/v1/tenants/01935500-0000-7000-8000-000000000001/provisioning")]
    [InlineData("POST", "/v1/tenants/01935500-0000-7000-8000-000000000001/retry-initial-admin")]
    [InlineData("POST", "/v1/tenants/01935500-0000-7000-8000-000000000001/activate")]
    [InlineData("POST", "/v1/tenants/01935500-0000-7000-8000-000000000001/suspend")]
    [InlineData("GET", "/tenants")]
    [InlineData("POST", "/tenants")]
    [InlineData("GET", "/api/tenants")]
    public async Task AdministrativeTenantRoutes_AreNotExposedAnonymously(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Role", "SuperAdmin");
        request.Headers.Add("X-Tenant-Id", "clinica-sintetica-01");

        var response = await _client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
