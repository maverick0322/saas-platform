using Microsoft.EntityFrameworkCore;
using Saas.Platform.Api.Features.Provisioning;
using Saas.Platform.Api.Persistence;

namespace Saas.Platform.Api;

public enum PlatformRuntimeMode
{
    Api = 0,
    Worker = 1
}

/// <summary>
/// Separa la composición de ejecución de la API pública HTTP y del proceso trabajador de aprovisionamiento
/// dentro del mismo binario/imagen de Platform (ADR-0003).
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
}
