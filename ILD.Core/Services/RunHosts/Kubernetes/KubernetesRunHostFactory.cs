using ILD.Core.Services.Interfaces;
using k8s;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Kubernetes;

public static class KubernetesRunHostFactory
{
    /// <summary>
    /// The host for <c>ILD_RUN_HOST=kubernetes</c>: loop runs go to pods, and what is not a loop run stays on <paramref name="local"/>.
    /// Reaches the cluster with ILD's own service account, or with the kubeconfig the settings name.
    /// </summary>
    public static RoutingRunHost Create(IRunHost local, Func<string, string?> setting, IProcessRunner runner, ILoggerFactory loggers,
        Func<CancellationToken, Task<IReadOnlySet<Guid>>>? retainedRuns = null)
    {
        var options = KubernetesRunHostOptions.From(setting);
        var configuration = options.Kubeconfig is { } kubeconfig
            ? KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfig)
            : KubernetesClientConfiguration.InClusterConfig();
        var namespaceName = options.Namespace ?? configuration.Namespace
            ?? throw new InvalidOperationException($"{KubernetesRunHostOptions.NamespaceSetting} is not set and ILD's own namespace could not be read.");
        var host = new KubernetesRunHost(new k8s.Kubernetes(configuration), options, namespaceName, runner, loggers.CreateLogger<KubernetesRunHost>())
        {
            RetainedRuns = retainedRuns ?? (_ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())),
        };
        return new RoutingRunHost(local, host);
    }
}
