using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations;

public class AgentAdapterRegistry : IAgentAdapterRegistry
{
    private readonly IServiceProvider _sp;
    private readonly Dictionary<string, Type> _adapterTypes = new();

    public AgentAdapterRegistry(IServiceProvider sp, IEnumerable<IAgentAdapter> adapters)
    {
        _sp = sp;
        foreach (var adapter in adapters)
        {
            var adapterType = adapter.GetType();
            foreach (var type in adapter.SupportedProviderTypes)
            {
                _adapterTypes[type] = adapterType;
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

    public string[] GetAllSupportedProviderTypes()
    {
        return _adapterTypes.Keys.ToArray();
    }
}
