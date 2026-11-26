using Saas.Platform.Api;
using Saas.Platform.Api.Features.Provisioning;

var runtimeMode = PlatformHostFactory.ResolveRuntimeMode(args);
if (runtimeMode == PlatformRuntimeMode.RecoverPlat05bSerialization)
{
    if (!PlatformHostFactory.TryResolveOperationIdArg(args, out var operationId))
    {
        Console.Error.WriteLine("Missing or invalid --operation-id=<uuid> for --mode=recover-plat05b-serialization.");
        Environment.ExitCode = 1;
        return;
    }

    using var recoveryHost = PlatformHostFactory.CreateRecoverPlat05bSerializationHost(args);
    await using var scope = recoveryHost.Services.CreateAsyncScope();
    var handler = scope.ServiceProvider.GetRequiredService<RecoverPlat05bSerializationIncidentHandler>();
    var result = await handler.HandleAsync(operationId, CancellationToken.None);
    Environment.ExitCode = result.Recovered ? 0 : 1;
    return;
}

if (runtimeMode == PlatformRuntimeMode.ResumeOperation)
{
    if (!PlatformHostFactory.TryResolveOperationIdArg(args, out var operationId))
    {
        Console.Error.WriteLine("Missing or invalid --operation-id=<uuid> for --mode=resume-operation.");
        Environment.ExitCode = 1;
        return;
    }

    using var resumeHost = PlatformHostFactory.CreateResumeOperationHost(args);
    await using var scope = resumeHost.Services.CreateAsyncScope();
    var handler = scope.ServiceProvider.GetRequiredService<ResumeProvisioningOperationHandler>();
    var result = await handler.HandleAsync(operationId, CancellationToken.None);
    Environment.ExitCode = result.Resumed ? 0 : 1;
    return;
}

if (runtimeMode == PlatformRuntimeMode.Worker)
{
    using var workerHost = PlatformHostFactory.CreateWorkerHost(args);
    await workerHost.RunAsync();
    return;
}

await using var apiApp = PlatformHostFactory.CreateApiApplication(args);
await apiApp.RunAsync();

public partial class Program;
