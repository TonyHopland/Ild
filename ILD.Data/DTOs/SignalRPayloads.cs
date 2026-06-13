using ILD.Data.Enums;

namespace ILD.Data.DTOs.SignalRPayloads;

public record NodeStateChangedPayload(Guid RunId, Guid NodeId, LoopRunNodeStatus OldStatus, LoopRunNodeStatus NewStatus);

public record LoopRunStateChangedPayload(Guid RunId, LoopRunStatus OldStatus, LoopRunStatus NewStatus);

public record EventLoggedPayload(Guid RunId, string Message, string EventType, Guid? NodeId, Guid? RunNodeId);

public record RunPausedPayload(Guid RunId);

public record RunResumedPayload(Guid RunId);

public record RunHaltedPayload(Guid RunId);

public record WorkItemStateChangedPayload(string WorkItemId, WorkItemStatus OldStatus, WorkItemStatus NewStatus);

public record DependencyResolvedPayload(string WorkItemId);

public record HumanFeedbackRequiredPayload(string WorkItemId, string Reason);

public record PreviewStateChangedPayload(string WorkItemId);

public record NodeProgressPayload(Guid RunId, Guid NodeId, string Line, long Seq);

public record SchedulerStateChangedPayload(bool IsPaused, int MaxConcurrent);
