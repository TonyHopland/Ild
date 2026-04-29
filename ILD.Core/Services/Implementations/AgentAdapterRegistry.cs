using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations;

public class AgentAdapterRegistry : IAgentAdapterRegistry
{
    private readonly IServiceProvider _sp;
    private readonly Dictionary<string, Type> _adapterTypes = new();

    public AgentAdapterRegistry(IServiceProvider sp, IEnumerable<ServiceDescriptor> descriptors)
    {
        _sp = sp;
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ServiceType == typeof(IAgentAdapter) && descriptor.ImplementationType != null)
            {
                var instance = Activator.CreateInstance(descriptor.ImplementationType, new object[0]) as IAgentAdapter;
                if (instance != null)
                {
                    foreach (var type in instance.SupportedProviderTypes)
                    {
                        _adapterTypes[type] = descriptor.ImplementationType;
                    }
                }
            }
        }
    }

    public Func<IAgentAdapter> ResolveForProvider(AiProvider provider)
    {
        if (!_adapterTypes.TryGetValue(provider.Type, out var adapterType))
            throw new InvalidOperationException(
                $"No adapter registered for provider type '{provider.Type}'");

        return () => (IAgentAdapter)ActivatorUtilities.CreateInstance(_sp, adapterType);
    }
}
