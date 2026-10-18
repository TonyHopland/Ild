using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// An agent's suggested new value for a <see cref="GlobalVariable"/>, applied
/// only if a human approves it (ADR-0027). The value is the whole new text.
/// A name no variable has suggests creating one. The snapshot is the variable's
/// <see cref="GlobalVariable.UpdatedAt"/> when the suggestion was made (null when
/// it did not exist): approving compares the variable against it, so a write
/// made since is never overwritten.
/// The source run or chat is only referenced, never owned: deleting it leaves
/// the suggestion to be decided on the Global Variables page.
/// </summary>
public class GlobalVariableProposal : IAgentProposal<GlobalVariableProposalStatus>
{
    public const int MaxRationaleLength = 2000;

    [Key]
    public Guid Id { get; set; }

    /// <summary>As the agent wrote it; the name a missing variable is created with.</summary>
    [Required]
    [MaxLength(GlobalVariable.MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(GlobalVariable.MaxNameLength)]
    public string NormalizedName { get; set; } = string.Empty;

    [Required]
    [MaxLength(GlobalVariable.MaxValueLength)]
    public string Value { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxRationaleLength)]
    public string Rationale { get; set; } = string.Empty;

    public DateTime? SnapshotUpdatedAt { get; set; }

    /// <summary>The loop run that suggested it. Mutually exclusive with <see cref="CreatedByChatSessionId"/>.</summary>
    public Guid? CreatedByLoopRunId { get; set; }

    /// <summary>The node execution of <see cref="CreatedByLoopRunId"/> running when it was made; null when none was.</summary>
    public Guid? CreatedByRunNodeId { get; set; }

    public Guid? CreatedByChatSessionId { get; set; }

    /// <summary>The sequence of the reply, in <see cref="CreatedByChatSessionId"/>, that the suggestion follows.</summary>
    public int? ChatReplySequence { get; set; }

    public GlobalVariableProposalStatus Status { get; set; } = GlobalVariableProposalStatus.Pending;

    public static ProposalStatuses<GlobalVariableProposalStatus> Statuses { get; } = new()
    {
        Pending = GlobalVariableProposalStatus.Pending,
        Rejected = GlobalVariableProposalStatus.Rejected,
        Stale = GlobalVariableProposalStatus.Stale,
        Superseded = GlobalVariableProposalStatus.Superseded,
    };

    [MaxLength(MaxRationaleLength)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// When the suggesting chat was told the decision. Null while undecided, and
    /// while a decision is still owed to the chat.
    /// </summary>
    public DateTime? DecisionDeliveredAt { get; set; }
}
