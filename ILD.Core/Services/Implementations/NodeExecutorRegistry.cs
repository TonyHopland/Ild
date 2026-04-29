using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class NodeExecutorRegistry : INodeExecutorRegistry
{
    private readonly IReadOnlyDictionary<NodeType, INodeExecutor> _executors;

    public NodeExecutorRegistry(IEnumerable<INodeExecutor> executors)
    {
        _executors = executors.ToDictionary(e => e.NodeType);
    }

    public INodeExecutor Get(NodeType type)
    {
        if (!_executors.TryGetValue(type, out var exec))
            throw new InvalidOperationException($"No executor registered for node type {type}");
        return exec;
    }
}
