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
/// Discriminated union of node-execution outcomes streamed by an
/// <see cref="INodeExecutor"/>. The engine routes purely on this type and
/// never inspects the underlying <c>NodeType</c>.
/// </summary>
public abstract record NodeOutcome
{
    /// <summary>
    /// Node is about to commit to executing. The engine creates a
    /// <c>LoopRunNode</c> row with <paramref name="EffectiveInput"/> as the
    /// recorded input. Must be yielded before any Success/Fail/Terminal.
    /// </summary>
    public sealed record NodeStarting(string? EffectiveInput = null) : NodeOutcome;

    /// <summary>Node finished successfully; engine follows the named edge.</summary>
    public sealed record Success(EdgeType Edge, string? Output = null) : NodeOutcome;

    /// <summary>Node failed; engine follows the named edge.</summary>
    public sealed record Fail(EdgeType Edge, string Reason, string? Output = null) : NodeOutcome;

    /// <summary>
    /// Node is awaiting an external action (human response, webhook). The
    /// engine parks the run in <c>WaitingHuman</c> and the work item in
    /// <c>HumanFeedback</c>. When the signal arrives the engine re-enters
    /// the node and the node consumes <c>Run.ExternalActionResult</c>.
    /// </summary>
    public sealed record WaitingAction(string Reason, string? Output = null) : NodeOutcome;

    /// <summary>
    /// Node could not proceed because an internal resource (e.g. AI provider
    /// capacity) is unavailable. The engine parks the work item in
    /// <c>WaitingForIld</c>; the scheduler resumes when the resource frees.
    /// The run stays <c>Running</c> from the user's perspective.
    /// </summary>
    public sealed record WaitingIld(string Reason) : NodeOutcome;

    /// <summary>
    /// Terminal node: the run is complete. No outgoing edge is followed.
    /// </summary>
    public sealed record Terminal(string? Output = null) : NodeOutcome;
}

/// <summary>
/// Slim execution context. The node resolves its own dependencies via the
/// service provider and reads run state directly from <see cref="Run"/>.
/// </summary>
public sealed record NodeExecutionContext(
    LoopRun Run,
    LoopNode Node,
    IServiceProvider Services,
    CancellationToken CancellationToken,
    Func<string, Task>? ProgressCallback = null);

/// <summary>
/// Stateful generator interface. The engine iterates the returned
/// <see cref="IAsyncEnumerable{NodeOutcome}"/>; each yielded outcome drives a
/// state transition. Implementations must be free of persistence side
/// effects — they read context, optionally call adapters/processes, and
/// yield outcomes. The engine is responsible for all DB writes, event log
/// entries, SignalR notifications, and routing.
/// </summary>
public interface INodeExecutor
{
    NodeType NodeType { get; }
    IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx);
}

public interface INodeExecutorRegistry
{
    INodeExecutor Get(NodeType type);
}
