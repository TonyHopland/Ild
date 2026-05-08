namespace ILD.WorkItemServer.Domain;

/// <summary>
/// Lifecycle states for a remote work item. The state machine is permissive:
/// any state can transition to any other, with auto-advance through
/// intermediates and validation only on Running claims.
/// </summary>
public enum WorkItemStatus
{
    Backlog = 0,
    WorkQueue = 1,
    Ready = 2,
    Running = 3,
    HumanFeedback = 4,
    WaitingForIld = 5,
    Done = 6,
}
