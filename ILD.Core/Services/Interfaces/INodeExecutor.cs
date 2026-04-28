using ILD.Core.Enums;
using ILD.Core.Models;

namespace ILD.Core.Services.Interfaces;

public sealed record NodeExecutionResult(bool Success, string? Output = null, string? Error = null)
{
    public static NodeExecutionResult Ok(string? output = null) => new(true, output);
    public static NodeExecutionResult Fail(string error, string? output = null) => new(false, output, error);
}

public sealed record NodeExecutionContext(
    LoopRun Run,
    LoopRunNode RunNode,
    LoopNode Node,
    WorkItem WorkItem,
    string? PreviousNodeOutput,
    CancellationToken CancellationToken);

public interface INodeExecutor
{
    NodeType NodeType { get; }
    Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx);
}

public interface INodeExecutorRegistry
{
    INodeExecutor Get(NodeType type);
}
