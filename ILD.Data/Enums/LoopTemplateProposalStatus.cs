namespace ILD.Data.Enums;

public enum LoopTemplateProposalStatus
{
    Pending = 0,

    /// <summary>Saved as a new version, as proposed or after edits in the Loop Editor.</summary>
    Applied = 1,

    Rejected = 2,

    /// <summary>
    /// The template moved on: a newer version was saved, or the template was
    /// deleted. Closed for good; the agent proposes again against the latest version.
    /// </summary>
    Stale = 3,

    /// <summary>
    /// Replaced by a newer proposal from the same source for the same template.
    /// Nobody decided it, so it is never announced to the source.
    /// </summary>
    Superseded = 4,
}
