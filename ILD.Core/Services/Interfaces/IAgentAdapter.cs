using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

public interface IAgentAdapter
{
    string Name { get; }
    string[] SupportedProviderTypes { get; }
    ConfigFieldDescriptor[] ConfigSchema { get; }
    AdapterModelSupport ModelSupport { get; }

    /// <summary>What this adapter declares about its provider type; see <see cref="AgentAdapterTraits"/>.</summary>
    AgentAdapterTraits Traits { get; }

    Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context);
}
