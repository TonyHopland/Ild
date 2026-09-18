using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

public interface IAgentAdapterRegistry
{
    Func<IAgentAdapter> ResolveForProvider(AiProvider provider);
    string[] GetAllSupportedProviderTypes();

    /// <summary>
    /// What the adapter backing <paramref name="providerType"/> declares about a
    /// model selector. <see cref="AdapterModelSupport.Unsupported"/> for an
    /// unregistered type, so a caller never has to special-case one.
    /// </summary>
    AdapterModelSupport GetModelSupport(string providerType);
}
