using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Container;

public static class ContainerRunHostFactory
{
    /// <summary>
    /// The host for ILD in its image outside Kubernetes: loop runs go to containers of the engine whose socket the settings name,
    /// and what is not a loop run stays on <paramref name="local"/>.
    /// </summary>
    public static RoutingRunHost Create(IRunHost local, Func<string, string?> setting, IProcessRunner runner, ILoggerFactory loggers,
        Func<CancellationToken, Task<IReadOnlySet<Guid>>>? retainedRuns = null, ILD.Core.Services.Implementations.Network.EgressCredentials? credentials = null)
    {
        var host = new ContainerRunHost(ContainerRunHostOptions.From(setting), runner, loggers.CreateLogger<ContainerRunHost>())
        {
            Credentials = credentials ?? ILD.Core.Services.Implementations.Network.EgressCredentials.Random(),
            RetainedRuns = retainedRuns ?? (_ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())),
        };
        return new RoutingRunHost(local, host);
    }
}
