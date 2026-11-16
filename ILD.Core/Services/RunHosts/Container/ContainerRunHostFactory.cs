using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts.Isolated;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Container;

public static class ContainerRunHostFactory
{
    /// <summary>
    /// The host for ILD in its image outside Kubernetes: loop runs go to containers of the engine whose socket the settings name,
    /// and what is not a loop run stays on <paramref name="local"/>.
    /// </summary>
    public static RoutingRunHost Create(IRunHost local, Func<string, string?> setting, IProcessRunner runner, ILoggerFactory loggers,
        Func<CancellationToken, Task<IReadOnlySet<Guid>>>? retainedRuns = null, ILD.Core.Services.Implementations.Network.EgressCredentials? credentials = null, ExecRunHostServices? services = null)
    {
        var host = new ContainerRunHost(ContainerRunHostOptions.From(setting), runner, loggers.CreateLogger<ContainerRunHost>())
        {
            ReadLimits = RunReadLimits.FromEnvironment(setting),
            Credentials = credentials ?? ILD.Core.Services.Implementations.Network.EgressCredentials.Random(),
            RetainedRuns = retainedRuns ?? (_ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())),
            Forwards = services?.Forwards,
            Probe = services?.Probe,
            Refusals = services?.Refusals,
            PreviewData = services?.PreviewData,
        };
        return new RoutingRunHost(local, host);
    }
}
