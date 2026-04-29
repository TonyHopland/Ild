using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

public interface IAgentAdapterRegistry
{
    Func<IAgentAdapter> ResolveForProvider(AiProvider provider);
}
