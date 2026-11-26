using k8s;
using k8s.Exceptions;

namespace Saas.Platform.Api.Features.Provisioning;

/// <summary>
/// Proveedor compartido de cliente Kubernetes in-cluster (<c>ServiceAccount</c>) para los adaptadores del Worker.
/// </summary>
internal interface IKubernetesClientAccessor
{
    IKubernetes GetOrCreateClient();
}

internal sealed class KubernetesInClusterClientAccessor : IKubernetesClientAccessor, IDisposable
{
    private readonly Lock _sync = new();
    private IKubernetes? _client;

    public IKubernetes GetOrCreateClient()
    {
        lock (_sync)
        {
            if (_client is not null)
            {
                return _client;
            }

            try
            {
                var config = KubernetesClientConfiguration.InClusterConfig();
                _client = new Kubernetes(config);
                return _client;
            }
            catch (KubeConfigException ex)
            {
                throw new InvalidOperationException(
                    "Kubernetes in-cluster ServiceAccount configuration is unavailable for CloudNativePG provisioning.",
                    ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
