namespace ILD.Data.Enums;

/// <summary>
/// Canonical values written to <c>WorkItem.HumanFeedbackReason</c>. The
/// frontend keys UI affordances (textarea vs. cleanup buttons, badge colour)
/// off these exact strings, so the backend MUST emit them verbatim.
/// </summary>
public static class HumanFeedbackReasons
{
    /// <summary>Run is parked at a Human node awaiting free-form input.</summary>
    public const string HumanInputNeeded = "Human Input Needed";

    /// <summary>Run is parked at a PR node awaiting an external merge / reject signal.</summary>
    public const string PrAwaitingMerge = "PR Awaiting Merge";

    /// <summary>A node failed and there is no on_failure edge to follow.</summary>
    public const string NodeFailed = "Node Failed";

    /// <summary>Recovery on startup left the run in a state that needs review.</summary>
    public const string RecoveryRequiresReview = "Recovery requires review";

    /// <summary>Run was cancelled and parked for cleanup.</summary>
    public const string RunCancelled = "Run Cancelled";

    /// <summary>Default for manual transitions / unspecified callers.</summary>
    public const string Manual = "manual";
}
