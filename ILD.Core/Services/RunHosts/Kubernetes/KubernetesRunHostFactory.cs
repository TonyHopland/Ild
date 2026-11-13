using ILD.Core.Services.Interfaces;
using k8s;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Kubernetes;

public static class KubernetesRunHostFactory
{
    /// <summary>
    /// The host for ILD in its image in a Kubernetes cluster: loop runs go to pods, and what is not a loop run stays on <paramref name="local"/>.
    /// Reaches the cluster with ILD's own service account, or with the kubeconfig the settings name.
    /// </summary>
    public static RoutingRunHost Create(IRunHost local, Func<string, string?> setting, IProcessRunner runner, ILoggerFactory loggers,
        Func<CancellationToken, Task<IReadOnlySet<Guid>>>? retainedRuns = null, ILD.Core.Services.Implementations.Network.EgressCredentials? credentials = null)
    {
        var options = KubernetesRunHostOptions.From(setting);
        // The in-cluster configuration's namespace is ILD's own; a kubeconfig's is only a default, so there ILD must be told.
        if (options.IldNamespace.Length == 0 && options.Kubeconfig is not null)
            throw new InvalidOperationException($"{KubernetesRunHostOptions.IldNamespaceSetting} is required with {KubernetesRunHostOptions.KubeconfigSetting}: the run's network policy admits ILD's pods only from the namespace ILD runs in.");
        var configuration = options.Kubeconfig is { } kubeconfig
            ? KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfig)
            : KubernetesClientConfiguration.InClusterConfig();
        var namespaceName = options.Namespace ?? configuration.Namespace
            ?? throw new InvalidOperationException($"{KubernetesRunHostOptions.NamespaceSetting} is not set and ILD's own namespace could not be read.");
        if (options.IldNamespace.Length == 0)
            options = options with { IldNamespace = configuration.Namespace ?? throw new InvalidOperationException($"{KubernetesRunHostOptions.IldNamespaceSetting} is not set and ILD's own namespace could not be read.") };
        var host = new KubernetesRunHost(new k8s.Kubernetes(configuration), options, namespaceName, runner, loggers.CreateLogger<KubernetesRunHost>())
        {
            ReadLimits = RunReadLimits.FromEnvironment(setting),
            Credentials = credentials ?? ILD.Core.Services.Implementations.Network.EgressCredentials.Random(),
            RetainedRuns = retainedRuns ?? (_ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())),
        };
        return new RoutingRunHost(local, host);
    }
}
