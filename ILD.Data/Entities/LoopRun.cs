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

    /// <summary>
    /// When true the run was halted by a human watching the live view: the
    /// in-flight AI node was interrupted and the run parked at
    /// <see cref="LoopRunStatus.WaitingHuman"/> awaiting a steer/resume. Cleared
    /// when the run resumes. Distinguishes a halted run from a node-driven
    /// WaitingHuman park (Human/PR node) so the UI can show the steer window.
    /// </summary>
    public bool IsHalted { get; set; }

    /// <summary>
    /// The live AI session id captured mid-stream by the active adapter, so a
    /// halted run can be resumed against the SAME agent session. Written by the
    /// AI node executor in its own DI scope as the session id arrives.
    /// </summary>
    [MaxLength(256)]
    public string? CurrentAiSessionId { get; set; }

    /// <summary>
    /// One-shot guidance the human supplied when resuming a halted AI node. The
    /// AI node executor consumes it as the next message to the resumed session
    /// and clears it so a later visit doesn't re-apply it.
    /// </summary>
    public string? SteeringNote { get; set; }

    /// <summary>
    /// When true the run is pinned: the worktree retention sweeper never
    /// reclaims its worktree/branch nor deletes the run. Stays pinned until a
    /// human clears the mark. See ADR-0008.
    /// </summary>
    public bool Retain { get; set; }

    public int NodeExecutionCount { get; set; }

    public int NextEventSeq { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Guid? CurrentNodeId { get; set; }

    // Transient bridge: the LoopNodeEdge the engine just traversed to reach
    // CurrentNodeId. Set by the engine when it follows an outgoing edge, then
    // consumed and cleared when the destination node's first LoopRunNode row
    // is created. Persisted so that a crash between edge-resolution and
    // run-node creation doesn't lose the edge attribution.
    public Guid? IncomingEdgeId { get; set; }

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

    // Discriminates which action the external actor took.
    public ExternalActionResultType ExternalActionResultType { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(LoopTemplateVersionId))]
    public LoopTemplateVersion LoopTemplateVersion { get; set; } = null!;

    [InverseProperty("LoopRun")]
    public ICollection<LoopRunNode> RunNodes { get; set; } = new List<LoopRunNode>();

    [InverseProperty("LoopRun")]
    public ICollection<EventLog> EventLogs { get; set; } = new List<EventLog>();
}
