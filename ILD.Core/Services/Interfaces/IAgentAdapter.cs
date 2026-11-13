using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

public interface IAgentAdapter
{
    string Name { get; }
    string[] SupportedProviderTypes { get; }
    ConfigFieldDescriptor[] ConfigSchema { get; }
    AdapterModelSupport ModelSupport { get; }

    /// <summary>What this adapter declares about its provider type; see <see cref="AgentAdapterTraits"/>.</summary>
    AgentAdapterTraits Traits { get; }

    /// <summary>
    /// How a run of <paramref name="provider"/> gets its model credential, or null when this adapter does not say.
    /// </summary>
    CredentialPath? CredentialPathFor(AiProvider provider) => null;

    Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context);
}
