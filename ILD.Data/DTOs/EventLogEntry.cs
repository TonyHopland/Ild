namespace ILD.Data.DTOs;

public record EventLogEntry(
    Guid? LoopRunId,
    string EventType,
    string Data,
    Guid? RunNodeId = null,
    DateTime Timestamp = default
);
