using ILD.Data.Enums;

namespace ILD.Data.DTOs.SignalRPayloads;

public record NodeStateChangedPayload(Guid RunId, Guid NodeId, LoopRunNodeStatus OldStatus, LoopRunNodeStatus NewStatus);

public record LoopRunStateChangedPayload(Guid RunId, LoopRunStatus OldStatus, LoopRunStatus NewStatus);

public record EventLoggedPayload(Guid RunId, string Message, string EventType, Guid? NodeId, Guid? RunNodeId);

public record RunPausedPayload(Guid RunId);

public record RunResumedPayload(Guid RunId);

public record WorkItemStateChangedPayload(Guid WorkItemId, WorkItemStatus OldStatus, WorkItemStatus NewStatus);

public record DependencyResolvedPayload(Guid WorkItemId);

public record HumanFeedbackRequiredPayload(Guid WorkItemId, string Reason);

public record NodeProgressPayload(Guid RunId, Guid NodeId, string Line);
