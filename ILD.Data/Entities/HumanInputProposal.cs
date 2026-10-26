using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// An agent's suggested answer to the question a work item's run asks at a Human
/// node: the output to take and the text to pass along, delivered only if a human
/// approves it (ADR-0030). It targets the one visit of the node the run was parked
/// at when it was made, so it never answers a later question.
/// The source run or chat is only referenced, never owned.
/// </summary>
public class HumanInputProposal : IAgentProposal<HumanInputProposalStatus>
{
    public const int MaxRationaleLength = 2000;
    public const int MaxTextLength = 8192;
    public const int MaxOutputLength = 256;

    [Key]
    public Guid Id { get; set; }

    [Required]
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>The run parked at the question.</summary>
    public Guid RunId { get; set; }

    /// <summary>The parked visit (<see cref="LoopRunNode"/>) of the Human node: the one question this answers.</summary>
    public Guid RunNodeId { get; set; }

    /// <summary>The name of an outgoing edge of the node, as the work item's actions list it.</summary>
    [Required]
    [MaxLength(MaxOutputLength)]
    public string Output { get; set; } = string.Empty;

    /// <summary>The optional input or context passed with the output; empty is allowed.</summary>
    [MaxLength(MaxTextLength)]
    public string Text { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxRationaleLength)]
    public string Rationale { get; set; } = string.Empty;

    /// <summary>The loop run that suggested it. Mutually exclusive with <see cref="CreatedByChatSessionId"/>.</summary>
    public Guid? CreatedByLoopRunId { get; set; }

    public Guid? CreatedByChatSessionId { get; set; }

    public HumanInputProposalStatus Status { get; set; } = HumanInputProposalStatus.Pending;

    public static ProposalStatuses<HumanInputProposalStatus> Statuses { get; } = new()
    {
        Pending = HumanInputProposalStatus.Pending,
        Rejected = HumanInputProposalStatus.Rejected,
        Stale = HumanInputProposalStatus.Stale,
        Superseded = HumanInputProposalStatus.Superseded,
    };

    [MaxLength(MaxRationaleLength)]
    public string? RejectionReason { get; set; }

    /// <summary>Who approved or rejected it, as the decision service was told.</summary>
    [MaxLength(256)]
    public string? DecidedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>Unused: the source is not told the decision.</summary>
    public DateTime? DecisionDeliveredAt { get; set; }
}
