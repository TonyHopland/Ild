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

public record WorkItemRunProgressedPayload(string WorkItemId);

public record NodeProgressPayload(Guid RunId, Guid NodeId, string Line, long Seq);

public record PrSnapshotChangedPayload(Guid RunId);

public record SchedulerStateChangedPayload(bool IsPaused, int MaxConcurrent);

public record ChatMessageAppendedPayload(Guid ChatSessionId, ChatMessageView Message);

public record ChatTurnProgressPayload(Guid ChatSessionId, string Delta);

public record ChatTurnCompletedPayload(Guid ChatSessionId, bool Interrupted);

public record ChatLoopUpdatePayload(Guid ChatSessionId, string Document);

public record NetworkPolicyChangedPayload();

/// <summary>
/// <paramref name="Decision"/> is the <see cref="NetworkDecision"/> name, not the
/// enum: the hub serialises enums as numbers where the REST API sends names, and
/// the Settings page reads one shape from both.
/// </summary>
public record NetworkLogAppendedPayload(Guid Id, string Host, int Port, DateTime Timestamp, string Decision, Guid? AiProviderId);

public record NetworkLogClearedPayload();

/// <summary>
/// One line of the process's own log: what <c>GET /api/v1/logging/entries</c>
/// returns and what a live line arrives as, so the Logging settings page reads
/// one shape from both. <paramref name="Detail"/> is the exception the event
/// carried, when it carried one.
/// </summary>
public record LogEntryPayload(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string? Detail);
