using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Container;

public static class ContainerRunHostFactory
{
    /// <summary>
    /// The host for <c>ILD_RUN_HOST=container</c>: loop runs go to containers of the engine whose socket the settings name,
    /// and what is not a loop run stays on <paramref name="local"/>.
    /// </summary>
    public static RoutingRunHost Create(IRunHost local, Func<string, string?> setting, IProcessRunner runner, ILoggerFactory loggers,
        Func<CancellationToken, Task<IReadOnlySet<Guid>>>? retainedRuns = null)
    {
        var host = new ContainerRunHost(ContainerRunHostOptions.From(setting), runner, loggers.CreateLogger<ContainerRunHost>())
        {
            RetainedRuns = retainedRuns ?? (_ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())),
        };
        return new RoutingRunHost(local, host);
    }
}
