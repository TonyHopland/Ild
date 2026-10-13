namespace ILD.Data.Enums;

public enum GlobalVariableProposalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,

    /// <summary>
    /// The variable no longer matched the suggestion's snapshot: a human wrote
    /// it on the page, or another suggestion for it was approved first.
    /// </summary>
    Stale = 3,

    /// <summary>
    /// Replaced by a newer suggestion from the same source for the same
    /// variable. Nobody decided it, so it is never announced to the source.
    /// </summary>
    Superseded = 4,
}
