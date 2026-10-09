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
    PrQueuedWritesClaimed = 18,

    /// <summary>
    /// An agent answered a review item again, and the new answer took the place
    /// of the one it had queued. Records what the write said before and after,
    /// so a person who read the old answer can see what changed under its id.
    /// </summary>
    PrQueuedWriteReplaced = 19,

    /// <summary>
    /// An agent took back a write it had queued, before the PR node sent it.
    /// Records what it would have said.
    /// </summary>
    PrQueuedWriteWithdrawn = 20,

    /// <summary>An execution cut off before it finished: a halt, or the AI provider stopping it.</summary>
    NodeInterrupted = 21,

    /// <summary>
    /// The run stopped for a person without a node asking them anything: a halt,
    /// a provider interruption, the AI step cap, or a manual move to
    /// HumanFeedback. Carries the full reason; a Human or PR node asking is
    /// <see cref="HumanFeedbackRequested"/> instead.
    /// </summary>
    RunParked = 22,
}
