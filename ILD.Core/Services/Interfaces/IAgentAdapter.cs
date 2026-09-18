using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

public interface IAgentAdapter
{
    string Name { get; }
    string[] SupportedProviderTypes { get; }
    ConfigFieldDescriptor[] ConfigSchema { get; }
    AdapterModelSupport ModelSupport { get; }
    Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context);
}
