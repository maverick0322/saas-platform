using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Features.Tenants.AdminHttp;
using Saas.Platform.Api.Features.Tenants.GetTenantAccessState;
using Saas.Platform.Api.Features.Tenants.GetTenantById;
using Saas.Platform.Api.Features.Tenants.ListTenants;
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
        builder.Services.AddScoped<ListTenantsHandler>();

        configureBuilder?.Invoke(builder);

        var adminApiOptions = new AdminApiOptions();
        builder.Configuration.GetSection(AdminApiOptions.SectionName).Bind(adminApiOptions);

        if (adminApiOptions.Enabled)
        {
            ConfigureAdminHttpSecurity(builder);
        }

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

        if (adminApiOptions.Enabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        // Technical health endpoints remain anonymous and available in all API host configurations.
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

        if (adminApiOptions.Enabled)
        {
            app.MapAdminTenantEndpoints();
        }

        if (internalGrpcOptions.Enabled)
        {
            app.MapGrpcService<TenantAccessGrpcService>();
        }

        app.Logger.LogInformation(
            "Saas.Platform.Api initialized (AdminApi={AdminApiEnabled}, InternalGrpc={InternalGrpcEnabled}, GrpcPort={GrpcPort}).",
            adminApiOptions.Enabled,
            internalGrpcOptions.Enabled,
            internalGrpcOptions.Port);

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

        switch (workerOptions.Mode)
        {
            case ProvisioningExecutionMode.Unconfigured:
                builder.Services.AddSingleton<IProvisioningStageExecutor, UnconfiguredProvisioningStageExecutor>();
                break;

            case ProvisioningExecutionMode.CloudNativePg:
            {
                var cnpgOptions = new CloudNativePgOptions();
                builder.Configuration.GetSection(CloudNativePgOptions.SectionName).Bind(cnpgOptions);
                cnpgOptions.Validate();

                builder.Services.AddSingleton(cnpgOptions);
                builder.Services.AddSingleton<ICloudNativePgClusterClient, KubernetesCloudNativePgClusterClient>();
                builder.Services.AddSingleton<IProvisioningStageExecutor, CloudNativePgProvisioningStageExecutor>();
                break;
            }

            default:
                throw new InvalidOperationException($"Unsupported ProvisioningWorker:Mode '{workerOptions.Mode}'.");
        }

        builder.Services.AddScoped<ProvisioningLeaseStore>();
        builder.Services.AddScoped<ProvisioningOperationProcessor>();
        builder.Services.AddHostedService<ProvisioningWorkerHostedService>();

        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }

    private static void ConfigureAdminHttpSecurity(WebApplicationBuilder builder)
    {
        var jwtOptions = new JwtValidationOptions();
        builder.Configuration.GetSection(JwtValidationOptions.SectionName).Bind(jwtOptions);
        var signingKey = jwtOptions.ValidateAndCreateSigningKey(builder.Configuration);

        var identityAuthOptions = new IdentityAuthOptions();
        builder.Configuration.GetSection(IdentityAuthOptions.SectionName).Bind(identityAuthOptions);
        var identityBaseUri = identityAuthOptions.ValidateAndGetBaseUri();

        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(JwtValidationOptions.DefaultClockSkewSeconds),
                    RoleClaimType = "role"
                };

                TenantAdminEndpoints.ConfigureJwtBearerEvents(options);
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AdminAuthPolicies.SuperAdmin, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(IdentityClosedRoles.SuperAdmin);
            });

        builder.Services
            .AddHttpClient<IIdentityAccountVerifier, IdentityMeAccountVerifier>(client =>
            {
                client.BaseAddress = identityBaseUri;
                client.Timeout = TimeSpan.FromSeconds(identityAuthOptions.TimeoutSeconds);
                client.MaxResponseContentBufferSize = IdentityMeAccountVerifier.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
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
