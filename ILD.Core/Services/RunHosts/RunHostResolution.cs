using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts.Local;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.RunHosts;

public static class RunHostResolution
{
    /// <summary>
    /// The run host registered with <paramref name="services"/>, or the local one over what it has for a
    /// container that registers none (a unit test's); the setting only ever picks the registered one.
    /// </summary>
    public static IRunHost GetRunHost(this IServiceProvider services)
        => services.GetService<IRunHost>() ?? new LocalRunHost(services.GetService<IProcessRunner>(), services);

    // The roles of the run host, for a caller that finds it through services: the role registered, or else the host (which is every role), so a test may register just the role it uses.

    public static IRunCommands GetRunCommands(this IServiceProvider services) => services.GetService<IRunCommands>() ?? services.GetRunHost();

    public static IRunWorkspace GetRunWorkspace(this IServiceProvider services) => services.GetService<IRunWorkspace>() ?? services.GetRunHost();

    public static IRunFiles GetRunFiles(this IServiceProvider services) => services.GetService<IRunFiles>() ?? services.GetRunHost();

    public static IRunLifecycle GetRunLifecycle(this IServiceProvider services) => services.GetService<IRunLifecycle>() ?? services.GetRunHost();

    public static IRunHolders GetRunHolders(this IServiceProvider services) => services.GetService<IRunHolders>() ?? services.GetRunHost();

    /// <summary>
    /// The operator view of the run host registered with <paramref name="services"/>: the one registered, else the registered host's
    /// own when it has one (so the two never name different hosts), else the local one's for a container that registers none.
    /// </summary>
    public static IRunHostOperator GetRunHostOperator(this IServiceProvider services)
        => services.GetService<IRunHostOperator>() ?? services.GetService<IRunHost>() as IRunHostOperator ?? LocalRunHost.Shared;
}
