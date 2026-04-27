namespace ILD.Core.Enums;

public enum EventType
{
    NodeStarted = 0,
    NodeCompleted = 1,
    NodeFailed = 2,
    EdgeTraversed = 3,
    LoopRunStarted = 4,
    LoopRunCompleted = 5,
    LoopRunFailed = 6,
    LoopRunCancelled = 7,
    HumanFeedbackRequested = 8,
    HumanFeedbackReceived = 9,
    RecoveryTriggered = 10,
    Error = 11
}
