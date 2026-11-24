using Saas.Platform.Api;

if (PlatformHostFactory.ResolveRuntimeMode(args) == PlatformRuntimeMode.Worker)
{
    using var workerHost = PlatformHostFactory.CreateWorkerHost(args);
    await workerHost.RunAsync();
    return;
}

await using var apiApp = PlatformHostFactory.CreateApiApplication(args);
await apiApp.RunAsync();

public partial class Program;
