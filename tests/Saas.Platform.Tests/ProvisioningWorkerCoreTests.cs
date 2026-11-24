using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Saas.Platform.Api;
using Saas.Platform.Api.Domain;
using Saas.Platform.Api.Features.Provisioning;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class ProvisioningWorkerCoreTests
{
    [Fact]
    public void ProvisioningWorkerOptions_DefaultsAreSafeAndBackoffIsBoundedExponential()
    {
        var options = new ProvisioningWorkerOptions();
        options.Validate();

        Assert.False(options.Enabled);
        Assert.Equal(3, options.MaxAutomaticRetries);
        Assert.Equal(4, options.MaxTotalAttempts);
        Assert.Equal(TimeSpan.FromSeconds(60), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(20), options.LeaseRenewalInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PollInterval);

        var shortLeaseOptions = new ProvisioningWorkerOptions { LeaseDurationSeconds = 1 };
        Assert.True(shortLeaseOptions.LeaseRenewalInterval >= TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(1000.0 / 3.0), shortLeaseOptions.LeaseRenewalInterval);

        Assert.Equal(TimeSpan.FromSeconds(15), options.CalculateRetryBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(30), options.CalculateRetryBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(60), options.CalculateRetryBackoff(3));
        Assert.Equal(TimeSpan.FromSeconds(120), options.CalculateRetryBackoff(4));
        Assert.Equal(TimeSpan.FromSeconds(240), options.CalculateRetryBackoff(5));
        Assert.Equal(TimeSpan.FromSeconds(300), options.CalculateRetryBackoff(6));
        Assert.Equal(TimeSpan.FromSeconds(300), options.CalculateRetryBackoff(20));
    }

    [Fact]
    public void ProvisioningWorkerOptions_InvalidValuesThrowInvalidOperationException()
    {
        var invalidRetries = new ProvisioningWorkerOptions { MaxAutomaticRetries = -1 };
        Assert.Throws<InvalidOperationException>(() => invalidRetries.Validate());

        var invalidLease = new ProvisioningWorkerOptions { LeaseDurationSeconds = 0 };
        Assert.Throws<InvalidOperationException>(() => invalidLease.Validate());

        var invalidMaxDelay = new ProvisioningWorkerOptions
        {
            InitialRetryDelaySeconds = 60,
            MaxRetryDelaySeconds = 30
        };
        Assert.Throws<InvalidOperationException>(() => invalidMaxDelay.Validate());
    }

    [Fact]
    public void ResolveRuntimeMode_DefaultsToApiAndSwitchesToWorkerExplicitly()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        Assert.Equal(PlatformRuntimeMode.Api, PlatformHostFactory.ResolveRuntimeMode([], emptyConfig));
        Assert.Equal(PlatformRuntimeMode.Worker, PlatformHostFactory.ResolveRuntimeMode(["--mode=worker"], emptyConfig));
        Assert.Equal(PlatformRuntimeMode.Api, PlatformHostFactory.ResolveRuntimeMode(["--mode=api"], emptyConfig));

        var workerConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:RuntimeMode"] = "Worker"
            })
            .Build();
        Assert.Equal(PlatformRuntimeMode.Worker, PlatformHostFactory.ResolveRuntimeMode([], workerConfig));
    }

    [Fact]
    public void CreateWorkerHost_UsesUnconfiguredAdapterDisabledByDefaultAndHasNoHttpServer()
    {
        using var host = PlatformHostFactory.CreateWorkerHost(
            [
                "--ConnectionStrings:PlatformDatabase=Host=localhost;Port=5432;Database=saas_platform_design;Username=postgres"
            ]);

        var options = host.Services.GetRequiredService<ProvisioningWorkerOptions>();
        var executor = host.Services.GetRequiredService<IProvisioningStageExecutor>();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        var httpServer = host.Services.GetService<IServer>();

        Assert.False(options.Enabled);
        Assert.IsType<UnconfiguredProvisioningStageExecutor>(executor);
        Assert.False(executor.SupportsRealProvisioning);
        Assert.Contains(hostedServices, service => service is ProvisioningWorkerHostedService);
        Assert.Null(httpServer);
    }

    [Fact]
    public void TenantProvisioningOperation_SanitizesJwtsConnectionStringsAndSecrets()
    {
        var sanitized = TenantProvisioningOperation.SanitizeDiagnosticSummary(
            "Failure on Host=pg-tenant-01.internal;Port=5432;Database=vital_identity;Username=app;Password=SuperSecretValue123! " +
            "token=abc123 eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjMifQ.signature");

        Assert.DoesNotContain("SuperSecretValue123!", sanitized);
        Assert.DoesNotContain("pg-tenant-01.internal", sanitized);
        Assert.DoesNotContain("abc123", sanitized);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", sanitized);
        Assert.Contains("[REDACTED_CONNECTION_STRING]", sanitized);
        Assert.Contains("[REDACTED_JWT]", sanitized);
        Assert.Contains("[REDACTED]", sanitized);

        Assert.Equal("cnpg-cluster-clinica-01", TenantProvisioningOperation.SanitizeTechnicalReference("  cnpg-cluster-clinica-01  "));
        Assert.Throws<ArgumentException>(() =>
            TenantProvisioningOperation.SanitizeTechnicalReference("Host=localhost;Database=x;Password=secret"));
        Assert.Throws<ArgumentException>(() =>
            TenantProvisioningOperation.SanitizeTechnicalReference("ref\nwith-newline"));
    }
}
