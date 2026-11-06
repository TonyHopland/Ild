using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

public interface IAgentAdapterRegistry
{
    Func<IAgentAdapter> ResolveForProvider(AiProvider provider);
    string[] GetAllSupportedProviderTypes();

    /// <summary>How a run of <paramref name="provider"/> gets its model credential, or null when its adapter does not say (ADR-0031).</summary>
    CredentialPath? GetCredentialPath(AiProvider provider) => null;

    /// <summary>
    /// What the adapter backing <paramref name="providerType"/> declares about a
    /// model selector. <see cref="AdapterModelSupport.Unsupported"/> for an
    /// unregistered type, so a caller never has to special-case one.
    /// </summary>
    AdapterModelSupport GetModelSupport(string providerType);

    /// <summary>
    /// What the adapter backing <paramref name="providerType"/> declares about its
    /// type, matched ignoring case and surrounding whitespace.
    /// <see cref="AgentAdapterTraits.None"/> for a null, blank or unregistered type,
    /// so a caller never has to special-case one.
    /// </summary>
    AgentAdapterTraits GetTraits(string? providerType);
}
