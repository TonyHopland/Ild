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
}
