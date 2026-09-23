namespace ILD.Data.Enums;

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
    Error = 11,
    CleanupStarted = 12,
    CleanupCompleted = 13,
    PrMerged = 14,
    PrMergeFailed = 15,
    BranchDeleteFailed = 16,

    /// <summary>
    /// A round read a review item, decided it needed no answer, and said so.
    /// The only durable trace of a judgement that produces no writing — without
    /// it, "considered and dismissed" is indistinguishable from "never read".
    /// </summary>
    PrReviewItemClosed = 17,

    /// <summary>
    /// The PR node took the round's queued writes and is about to send them.
    /// Recorded because the claim is irreversible: a crash between it and the
    /// forge loses those answers, and this is what keeps them visible to a
    /// person instead of vanishing while the round looks finished.
    /// </summary>
    PrQueuedWritesClaimed = 18
}
