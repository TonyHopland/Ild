namespace ILD.Data.Enums;

public enum HumanInputProposalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,

    /// <summary>
    /// The visit it answers is no longer open: someone answered it, or the run
    /// moved on, so approving it delivers nothing.
    /// </summary>
    Stale = 3,

    /// <summary>
    /// Replaced by a newer suggestion from the same source for the same work
    /// item. Nobody decided it.
    /// </summary>
    Superseded = 4,
}
