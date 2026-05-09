using ILD.Data.Enums;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Adapter-level result type shared with <c>IAgentAdapter</c>. Represents the
/// outcome of an inner unit of work (e.g. an LLM call, a process). Executors
/// translate this into the higher-level <see cref="NodeOutcome"/>.
/// </summary>
public sealed record NodeExecutionResult(bool Success, string? Output = null, string? Error = null, string? ResolvedPrompt = null, string? SessionId = null, string? IncomingSessionId = null)
{
    public static NodeExecutionResult Ok(string? output = null, string? resolvedPrompt = null, string? sessionId = null, string? incomingSessionId = null) => new(true, output, null, resolvedPrompt, sessionId, incomingSessionId);
    public static NodeExecutionResult Fail(string error, string? output = null) => new(false, output, error);
}

/// <summary>
/// Why a node has been suspended pending an external event. The engine treats
/// every <see cref="SuspendKind"/> the same (write WaitingHuman, surface human
/// feedback), but the kind is exposed for observability and future routing.
/// </summary>
public enum SuspendKind
{
    /// <summary>Awaiting input collected via the Human-feedback API.</summary>
    HumanInput = 0,
    /// <summary>Awaiting an asynchronous webhook / signal (e.g. PR merge).</summary>
    ExternalSignal = 1,
}

/// <summary>
/// Discriminated union of node-execution outcomes produced by an
/// <see cref="INodeExecutor"/>. The engine routes purely on this type and never
/// inspects the underlying <c>NodeType</c>.
/// </summary>
/// <summary>
/// Discriminated union of node-execution outcomes produced by an
/// <see cref="INodeExecutor"/>. The engine routes purely on this type and never
/// inspects the underlying <c>NodeType</c>.
///
/// The base record carries the common <c>Output</c> slot; case-specific data
/// (resolved prompt on success, reject reason on failure, etc.) lives on the
/// derived records.
/// </summary>
public abstract record NodeOutcome(string? Output = null)
{
    /// <summary>Node finished successfully; engine follows an <c>OnSuccess</c> edge.</summary>
    public sealed record Succeeded(string? Output = null, string? ResolvedPrompt = null, string? SessionId = null, string? IncomingSessionId = null) : NodeOutcome(Output);

    /// <summary>Node failed; engine retries (no failure edge) or follows <c>OnFailure</c>.</summary>
    public sealed record Failed(string Reason, string? Output = null) : NodeOutcome(Output);

    /// <summary>
    /// Node is paused awaiting an external event. The run is parked at this node
    /// until <c>ILoopEngine.SignalNodeResultAsync</c> is called.
    /// </summary>
    public sealed record Suspended(string Reason, SuspendKind Kind, string? Output = null) : NodeOutcome(Output);

    /// <summary>
    /// Terminal node: the run is complete on success of this node. There is no
    /// outgoing edge.
    /// </summary>
    public sealed record Terminal(string? Output = null) : NodeOutcome(Output);

    public static NodeOutcome FromResult(NodeExecutionResult r)
        => r.Success
            ? new Succeeded(r.Output, r.ResolvedPrompt, r.SessionId, r.IncomingSessionId)
            : new Failed(r.Error ?? "node failed", r.Output);

    // ---- Convenience accessors (test ergonomics) -------------------------

    /// <summary>True for non-failed outcomes (<see cref="Succeeded"/>, <see cref="Terminal"/>, <see cref="Suspended"/>).</summary>
    public bool Success => this is not Failed;

    /// <summary>The error message, set only on <see cref="Failed"/>.</summary>
    public string? Error => this is Failed f ? f.Reason : null;
}

public sealed record NodeExecutionContext(
    LoopRun Run,
    LoopRunNode RunNode,
    LoopNode Node,
    WorkItemView WorkItem,
    string? PreviousNodeOutput,
    CancellationToken CancellationToken,
    Func<string, Task>? ProgressCallback = null);

public interface INodeExecutor
{
    NodeType NodeType { get; }

    /// <summary>
    /// Execute the node. Implementations should not mutate run/runnode/workitem
    /// state directly — return a <see cref="NodeOutcome"/> and let the engine
    /// persist the transition.
    /// </summary>
    Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx);

    /// <summary>
    /// Build the JSON payload describing the effective input the node will run
    /// with (resolved command, prompt, etc.). Used by the engine for the
    /// <c>NodeStarted</c> event-log entry. Default implementation returns just
    /// the node type.
    /// </summary>
    string DescribeInput(NodeExecutionContext ctx)
        => System.Text.Json.JsonSerializer.Serialize(new { nodeType = NodeType.ToString() });
}

public interface INodeExecutorRegistry
{
    INodeExecutor Get(NodeType type);
}
