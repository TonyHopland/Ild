namespace ILD.Data.DTOs;

public record EventLogEntry(
    Guid? LoopRunId,
    string EventType,
    string Data,
    string? PayloadPath = null,
    Guid? RunNodeId = null
);
