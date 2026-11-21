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
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.Equal("saas-platform", root.GetProperty("service").GetString());
        Assert.Equal("live", root.GetProperty("probe").GetString());
        Assert.Equal("process-only", root.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task GetHealthReady_ReturnsOkWithProcessOnlyReadinessPayload()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.Equal("saas-platform", root.GetProperty("service").GetString());
        Assert.Equal("ready", root.GetProperty("probe").GetString());
        Assert.Equal("process-only", root.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task GetUnknownHealthRoute_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/health/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
