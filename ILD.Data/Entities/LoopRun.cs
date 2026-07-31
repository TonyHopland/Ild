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
    /// Who caused the halt, when <see cref="IsHalted"/> is set. Null means a
    /// human pressed Halt — which is what every row written before shutdown
    /// draining existed says, and what a run resumed by any path says again
    /// (every path that clears <see cref="IsHalted"/> nulls this too, or a
    /// later human halt would be auto-resumed out from under the person who
    /// asked for it).
    /// </summary>
    public HaltReason? HaltReason { get; set; }

    /// <summary>
    /// The run was parked by the shutdown drain, not by a person: the halt this
    /// process inflicted on itself on the way out, and the one it resumes on the
    /// next start. Computed rather than stored so the four readers that decide
    /// whether to auto-resume — recovery, the startup reconciler, the stuck-run
    /// watchdog and the drain's own tests — cannot drift apart.
    /// </summary>
    [NotMapped]
    public bool IsShutdownHalted =>
        Status == LoopRunStatus.WaitingHuman && IsHalted && HaltReason == Enums.HaltReason.Shutdown;

    /// <summary>
    /// The run needs a driver again and nobody else is coming for it: either a
    /// crash left it <see cref="LoopRunStatus.Running"/> with its driving loop
    /// gone, or the shutdown drain parked it on the way out. The two arrive at
    /// startup in different row shapes but want the same answer to the only
    /// question startup asks — is this ours to pick up? — so the shapes are
    /// spelled out once here rather than at each of the readers (recovery, the
    /// stuck-run watchdog), which would otherwise have to be edited in step
    /// whenever a halt reason or status is added.
    ///
    /// Deliberately <b>not</b> a database query: both callers already read the
    /// live set through <c>ILoopRunStore.GetActiveRunsAsync</c> and filter in
    /// memory, and translating this to SQL would put the same knowledge back in
    /// a second place.
    /// </summary>
    [NotMapped]
    public bool IsRecoverable => Status == LoopRunStatus.Running || IsShutdownHalted;

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

    // Name of the custom edge an external actor selected (e.g. a Human node's
    // named button). Null means the actor took the default success / fallback
    // outlet. Consumed alongside ExternalActionResult when a waiting node
    // re-enters and routes.
    [MaxLength(256)]
    public string? ExternalActionEdgeName { get; set; }

    // Last full PR snapshot (JSON-serialised RemotePrSnapshot) fetched by the
    // PR heartbeat poller while parked at a PR node. Drives the feedback UI's
    // full PR view. Null until the first poll.
    public string? PrSnapshot { get; set; }

    // Comma-separated set of PR edge-state names (on_*) that were true at the
    // previous poll, used to fire custom edges only on state transitions. Reset
    // to null when the run (re)parks at a PR node so an already-true state
    // counts as a transition on the first poll after parking.
    [MaxLength(512)]
    public string? PrPolledEdgeStates { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(LoopTemplateVersionId))]
    public LoopTemplateVersion LoopTemplateVersion { get; set; } = null!;

    [InverseProperty("LoopRun")]
    public ICollection<LoopRunNode> RunNodes { get; set; } = new List<LoopRunNode>();

    [InverseProperty("LoopRun")]
    public ICollection<EventLog> EventLogs { get; set; } = new List<EventLog>();

    [InverseProperty("LoopRun")]
    public ICollection<LoopRunVariable> Variables { get; set; } = new List<LoopRunVariable>();
}
