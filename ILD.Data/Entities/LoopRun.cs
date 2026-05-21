using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class LoopRun : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string WorkItemId { get; set; } = string.Empty;

    [Required]
    [ForeignKey("LoopTemplateVersion")]
    public Guid LoopTemplateVersionId { get; set; }

    public LoopRunStatus Status { get; set; }

    [Required]
    [MaxLength(128)]
    public RecoveryPolicy RecoveryPolicy { get; set; }

    public bool IsPaused { get; set; }

    public int NodeExecutionCount { get; set; }

    public int NextEventSeq { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Guid? CurrentNodeId { get; set; }

    [MaxLength(1024)]
    public string? WorktreePath { get; set; }

    [MaxLength(256)]
    public string? BranchName { get; set; }

    [MaxLength(2048)]
    public string? PrUrl { get; set; }

    public bool IsPrMerged { get; set; }

    public Guid? RepositoryId { get; set; }

    public Guid? CreatedByLoopRunId { get; set; }

    [MaxLength(512)]
    public string? HumanFeedbackReason { get; set; }

    // Output of the node on the most recently traversed incoming edge.
    // Source of truth for {{PreviousNode.Output}} in prompt rendering.
    public string? PreviousNodeOutput { get; set; }

    // Payload supplied by an external actor (human response, webhook signal)
    // while the run was parked at a waiting node.
    public string? ExternalActionResult { get; set; }

    // True when ExternalActionResult is a rejection (human "no", PR closed).
    public bool ExternalActionResultRejected { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(LoopTemplateVersionId))]
    public LoopTemplateVersion LoopTemplateVersion { get; set; } = null!;

    [InverseProperty("LoopRun")]
    public ICollection<LoopRunNode> RunNodes { get; set; } = new List<LoopRunNode>();

    [InverseProperty("LoopRun")]
    public ICollection<EventLog> EventLogs { get; set; } = new List<EventLog>();
}
