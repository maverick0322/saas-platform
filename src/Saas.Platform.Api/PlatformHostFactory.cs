using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.GetTenantAccessState;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.RegisterTenant;
using Saas.Platform.Api.Persistence;

namespace Saas.Platform.Api;

public enum PlatformRuntimeMode
{
    Api = 0,
    Worker = 1
}

/// <summary>
/// Separa la composición de ejecución de la API pública HTTP (+ gRPC interno opcional) y del
/// proceso trabajador de aprovisionamiento dentro del mismo binario/imagen de Platform (ADR-0003).
/// </summary>
public static class PlatformHostFactory
{
    public static PlatformRuntimeMode ResolveRuntimeMode(string[] args, IConfiguration? configuration = null)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--mode=worker", StringComparison.OrdinalIgnoreCase))
            {
                return PlatformRuntimeMode.Worker;
            }

            if (string.Equals(arg, "--mode=api", StringComparison.OrdinalIgnoreCase))
            {
                return PlatformRuntimeMode.Api;
            }
        }

        var rawMode = configuration?["Platform:RuntimeMode"]
            ?? Environment.GetEnvironmentVariable("Platform__RuntimeMode");

        if (string.Equals(rawMode, "Worker", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformRuntimeMode.Worker;
        }

        return PlatformRuntimeMode.Api;
    }

    public static WebApplication CreateApiApplication(
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
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

        configureBuilder?.Invoke(builder);

        var internalGrpcOptions = new InternalGrpcOptions();
        builder.Configuration.GetSection(InternalGrpcOptions.SectionName).Bind(internalGrpcOptions);

        if (internalGrpcOptions.Enabled)
        {
            var httpPort = builder.Configuration.GetValue("Ports:Http", InternalGrpcOptions.DefaultHttpPort);
            internalGrpcOptions.ValidateWhenEnabled(httpPort);

            builder.WebHost.ConfigureKestrel(kestrelOptions =>
            {
                kestrelOptions.ListenAnyIP(httpPort, listen => listen.Protocols = HttpProtocols.Http1);
                kestrelOptions.ListenAnyIP(internalGrpcOptions.Port, listen => listen.Protocols = HttpProtocols.Http2);
            });

            builder.Services.AddSingleton(internalGrpcOptions);
            builder.Services.AddScoped<GetTenantAccessStateHandler>();
            builder.Services.AddGrpc();
        }

        builder.Services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("Process is running."), tags: ["live", "ready"]);

        var app = builder.Build();

        // Technical health endpoints only. Administrative tenant HTTP endpoints are intentionally
        // deferred until SuperAdmin authentication and Identity contracts are ratified (PLAT-01 / PLAT-02A / PLAT-03A).
        // ProvisioningWorkerHostedService is intentionally excluded from the public API host (ADR-0003).
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

        if (internalGrpcOptions.Enabled)
        {
            app.MapGrpcService<TenantAccessGrpcService>();
            app.Logger.LogInformation(
                "Saas.Platform.Api initialized with technical health endpoints and internal TenantAccessService gRPC on HTTP/2 port {GrpcPort}.",
                internalGrpcOptions.Port);
        }
        else
        {
            app.Logger.LogInformation("Saas.Platform.Api initialized with technical health endpoints.");
        }

        return app;
    }

    public static IHost CreateWorkerHost(
        string[] args,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(args);

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

        var workerOptions = new ProvisioningWorkerOptions();
        builder.Configuration.GetSection(ProvisioningWorkerOptions.SectionName).Bind(workerOptions);
        workerOptions.Validate();

        builder.Services.AddSingleton(workerOptions);
        builder.Services.AddSingleton<IProvisioningStageExecutor, UnconfiguredProvisioningStageExecutor>();
        builder.Services.AddScoped<ProvisioningLeaseStore>();
        builder.Services.AddScoped<ProvisioningOperationProcessor>();
        builder.Services.AddHostedService<ProvisioningWorkerHostedService>();

        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }

    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report, string probe, string scope)
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
}
