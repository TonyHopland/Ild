using System.ComponentModel.DataAnnotations;

namespace ILD.WorkItemServer.Domain;

public enum WorkItemEditProposalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,

    /// <summary>
    /// The item's editable fields no longer matched the snapshot when a human
    /// tried to approve it, or another proposal on the item was approved first.
    /// </summary>
    Stale = 3,
}

/// <summary>
/// An agent's suggested edit to a work item, applied only if a human approves
/// it. Each proposed field is null when it was not proposed; a blank branch
/// override is a proposal to clear it. The snapshot is the five editable fields
/// as they were when the proposal was made: approving compares the item against
/// it, so a human edit made since is never overwritten.
/// </summary>
public class WorkItemEditProposal
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The owning <see cref="WorkItem.InternalId"/>.</summary>
    public int WorkItemId { get; set; }

    [MaxLength(512)]
    public string? ProposedTitle { get; set; }

    public string? ProposedDescription { get; set; }

    /// <summary>JSON-serialized string[]; null when tags were not proposed.</summary>
    public string? ProposedTagsJson { get; set; }

    [MaxLength(256)]
    public string? ProposedBranchNameOverride { get; set; }

    [MaxLength(256)]
    public string? ProposedBaseBranchOverride { get; set; }

    [Required]
    [MaxLength(512)]
    public string SnapshotTitle { get; set; } = string.Empty;

    public string? SnapshotDescription { get; set; }

    public string SnapshotTagsJson { get; set; } = "[]";

    [MaxLength(256)]
    public string? SnapshotBranchNameOverride { get; set; }

    [MaxLength(256)]
    public string? SnapshotBaseBranchOverride { get; set; }

    [MaxLength(2000)]
    public string? Rationale { get; set; }

    /// <summary>The loop run that proposed it. Mutually exclusive with <see cref="CreatedByChatSessionId"/>.</summary>
    public Guid? CreatedByLoopRunId { get; set; }

    public Guid? CreatedByChatSessionId { get; set; }

    public WorkItemEditProposalStatus Status { get; set; } = WorkItemEditProposalStatus.Pending;

    [MaxLength(2000)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// When the proposing chat was told the decision. Null while undecided, and
    /// while a decision is still owed to the chat.
    /// </summary>
    public DateTime? DecisionDeliveredAt { get; set; }
}
