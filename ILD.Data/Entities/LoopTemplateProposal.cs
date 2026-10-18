using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// An agent's proposed change to a saved <see cref="LoopTemplate"/>, saved only if
/// a human saves it (ADR-0028). The document is the whole proposed
/// <c>ild-loop-template/v2</c> document, upgraded and with its outputs normalised
/// as a save stores them. It was made against <see cref="BaseVersion"/>, which was
/// the template's latest version then, and it can only be saved while it still is.
/// The template and the source run or chat are only referenced, never owned:
/// deleting the source leaves the proposal to be decided in the Loop Editor.
/// </summary>
public class LoopTemplateProposal : IAgentProposal<LoopTemplateProposalStatus>
{
    public const int MaxRationaleLength = 2000;

    [Key]
    public Guid Id { get; set; }

    public Guid LoopTemplateId { get; set; }

    public int BaseVersion { get; set; }

    [Required]
    public string Document { get; set; } = string.Empty;

    /// <summary>
    /// The Edit List the document was made from, as JSON, as the server parsed it
    /// (null members dropped); null when the agent sent the whole document.
    /// </summary>
    public string? Edits { get; set; }

    [Required]
    [MaxLength(MaxRationaleLength)]
    public string Rationale { get; set; } = string.Empty;

    /// <summary>The loop run that proposed it. Mutually exclusive with <see cref="CreatedByChatSessionId"/>.</summary>
    public Guid? CreatedByLoopRunId { get; set; }

    /// <summary>The node execution of <see cref="CreatedByLoopRunId"/> running when it was made; null when none was.</summary>
    public Guid? CreatedByRunNodeId { get; set; }

    public Guid? CreatedByChatSessionId { get; set; }

    /// <summary>The sequence of the reply, in <see cref="CreatedByChatSessionId"/>, that the proposal follows.</summary>
    public int? ChatReplySequence { get; set; }

    public LoopTemplateProposalStatus Status { get; set; } = LoopTemplateProposalStatus.Pending;

    public static ProposalStatuses<LoopTemplateProposalStatus> Statuses { get; } = new()
    {
        Pending = LoopTemplateProposalStatus.Pending,
        Rejected = LoopTemplateProposalStatus.Rejected,
        Stale = LoopTemplateProposalStatus.Stale,
        Superseded = LoopTemplateProposalStatus.Superseded,
    };

    /// <summary>Applied with a loop that differs from the proposed document (layout and edge ids aside).</summary>
    public bool EditedBeforeSave { get; set; }

    /// <summary>The template version an Applied proposal was saved as.</summary>
    public int? AppliedVersion { get; set; }

    [MaxLength(MaxRationaleLength)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// When the proposing chat was told a human's decision. Null while undecided,
    /// while a decision is still owed to the chat, and always for Stale and
    /// Superseded, which no human decided.
    /// </summary>
    public DateTime? DecisionDeliveredAt { get; set; }
}
