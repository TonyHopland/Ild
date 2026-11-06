using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations;

public class AgentAdapterRegistry : IAgentAdapterRegistry
{
    private readonly IServiceProvider _sp;
    // Case-insensitive like the providers API and the tool catalog, so a provider
    // saved as "COPILOT" still reaches the Copilot adapter.
    private readonly Dictionary<string, Type> _adapterTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AdapterModelSupport> _modelSupport = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentAdapterTraits> _traits = new(StringComparer.OrdinalIgnoreCase);

    public AgentAdapterRegistry(IServiceProvider sp, IEnumerable<IAgentAdapter> adapters)
    {
        _sp = sp;
        foreach (var adapter in adapters)
        {
            var adapterType = adapter.GetType();
            foreach (var type in adapter.SupportedProviderTypes)
            {
                _adapterTypes[type] = adapterType;
                _modelSupport[type] = adapter.ModelSupport;
                _traits[type] = adapter.Traits;
            }
        }
    }

    public Func<IAgentAdapter> ResolveForProvider(AiProvider provider)
    {
        if (!_adapterTypes.TryGetValue(provider.Type, out var adapterType))
            throw new InvalidOperationException(
                $"No adapter registered for provider type '{provider.Type}'");

        return () =>
        {
            var adapter = (IAgentAdapter)ActivatorUtilities.CreateInstance(_sp, adapterType);
            return _sp.GetService<IManagedAgentUsage>() is { } usage
                ? new LeasingAdapter(adapter, usage, provider.Type)
                : adapter;
        };
    }

    /// <summary>Holds the managed agent's in-use lease for exactly the duration of each execution.</summary>
    private sealed class LeasingAdapter(IAgentAdapter inner, IManagedAgentUsage usage, string providerType) : IAgentAdapter
    {
        public string Name => inner.Name;
        public string[] SupportedProviderTypes => inner.SupportedProviderTypes;
        public ConfigFieldDescriptor[] ConfigSchema => inner.ConfigSchema;
        public AdapterModelSupport ModelSupport => inner.ModelSupport;
        public AgentAdapterTraits Traits => inner.Traits;
        public CredentialPath? CredentialPathFor(AiProvider provider) => inner.CredentialPathFor(provider);

        public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            using var lease = usage.Acquire(providerType);
            return await inner.ExecuteAsync(context);
        }
    }

    public CredentialPath? GetCredentialPath(AiProvider provider)
        => _adapterTypes.ContainsKey(provider.Type) ? ResolveForProvider(provider)().CredentialPathFor(provider) : null;

    public string[] GetAllSupportedProviderTypes()
    {
        return _adapterTypes.Keys.ToArray();
    }

    public AdapterModelSupport GetModelSupport(string providerType)
    {
        return string.IsNullOrWhiteSpace(providerType)
            ? AdapterModelSupport.Unsupported
            : _modelSupport.GetValueOrDefault(providerType, AdapterModelSupport.Unsupported);
    }

    public AgentAdapterTraits GetTraits(string? providerType)
    {
        return string.IsNullOrWhiteSpace(providerType)
            ? AgentAdapterTraits.None
            : _traits.GetValueOrDefault(providerType.Trim(), AgentAdapterTraits.None);
    }
}
