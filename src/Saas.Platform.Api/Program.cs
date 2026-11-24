using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Saas.Platform.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<PlatformDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("PlatformDatabase");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Connection string 'PlatformDatabase' is not configured. Provide it via standard configuration or external secrets.");
    }

    options.UseNpgsql(
        connectionString,
        npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
            PlatformDbContext.MigrationsHistoryTableName,
            PlatformDbContext.SchemaName));
});

builder.Services.AddScoped<RegisterTenantHandler>();
builder.Services.AddScoped<GetTenantByIdHandler>();

builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("Process is running."), tags: ["live", "ready"]);

var app = builder.Build();

// Technical health endpoints only. Administrative tenant HTTP endpoints are intentionally
// deferred until SuperAdmin authentication and Identity contracts are ratified (PLAT-01 / PLAT-02A).
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = (context, report) => WriteHealthResponseAsync(context, report, probe: "live", scope: "process-only")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = (context, report) => WriteHealthResponseAsync(context, report, probe: "ready", scope: "process-only")
});

app.Logger.LogInformation("Saas.Platform.Api initialized with technical health endpoints.");

app.Run();

static Task WriteHealthResponseAsync(HttpContext context, HealthReport report, string probe, string scope)
{
    context.Response.ContentType = "application/json; charset=utf-8";

    var payload = new
    {
        status = report.Status.ToString(),
        service = "saas-platform",
        probe,
        scope
    };

    return context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
}

public partial class Program;
