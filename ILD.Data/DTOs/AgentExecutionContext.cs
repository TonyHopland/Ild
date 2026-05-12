namespace ILD.Data.DTOs;

public record AgentExecutionContext(
    Entities.AiProvider Provider,
    string InitialPrompt,
    string SessionPrompt,
    LoopRunContext RunContext,
    int ExecutionCount,
    CancellationToken Cancel,
    Func<string, Task>? ProgressCallback = null,
    Dictionary<string, object?>? AdapterConfig = null,
    string? SessionId = null,
    string? IncomingSessionId = null,
    bool ManageSession = false
);
