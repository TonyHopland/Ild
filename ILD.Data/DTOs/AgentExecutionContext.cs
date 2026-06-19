namespace ILD.Data.DTOs;

public record AgentExecutionContext(
    Entities.AiProvider Provider,
    string Prompt,
    LoopRunContext RunContext,
    int ExecutionCount,
    CancellationToken Cancel,
    Func<string, Task>? ProgressCallback = null,
    Dictionary<string, object?>? AdapterConfig = null,
    IReadOnlyList<string>? ToolAllowlist = null,
    string? SessionId = null,
    string? IncomingSessionId = null,
    bool ManageSession = false,
    // Invoked by the adapter with the agent's session id the first time it is
    // seen mid-stream, so the run can be halted and later resumed against the
    // SAME session. Fires at most once per execution; best-effort (never throws).
    Action<string>? OnSessionId = null,
    // When set (managed sessions only), the adapter first materializes a copy of
    // this source session's transcript under <see cref="SessionId"/> — the fresh
    // destination id — leaving the source untouched, then continues on the copy.
    // This is the fork primitive; <see cref="SessionId"/> is the fork's id.
    string? ForkFromSessionId = null,
    // Set when this execution belongs to a standalone Chat Session rather than a
    // LoopRun (see ADR-0010). When present, managed-session snapshots are keyed on
    // the chat session, and the ILD MCP server is told the chat session id (so
    // created work items are stamped with it) instead of a loop-run id.
    Guid? ChatSessionId = null
);
