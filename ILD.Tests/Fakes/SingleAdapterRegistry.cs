using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests.Fakes;

/// <summary>Resolves every provider to the one adapter it was given.</summary>
internal sealed class SingleAdapterRegistry(IAgentAdapter adapter) : IAgentAdapterRegistry
{
    public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => adapter;
    public string[] GetAllSupportedProviderTypes() => adapter.SupportedProviderTypes;
    public AdapterModelSupport GetModelSupport(string providerType) => adapter.ModelSupport;
    public AgentAdapterTraits GetTraits(string? providerType) => adapter.Traits;
}
