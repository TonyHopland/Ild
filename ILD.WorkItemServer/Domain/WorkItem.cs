using System.ComponentModel.DataAnnotations;

namespace ILD.WorkItemServer.Domain;

/// <summary>
/// Server-side work item entity. RepositoryId is stored so clients can
/// retrieve it after the round-trip and attach it to LoopRun records.
/// </summary>
public class WorkItem
{
    [Key]
    public int InternalId { get; set; }

    [Required]
    [MaxLength(64)]
    public string Id { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [MaxLength(256)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;

    public WorkItemStatus Status { get; set; } = WorkItemStatus.Backlog;

    /// <summary>JSON-serialized string[] of tags.</summary>
    public string TagsJson { get; set; } = "[]";

    /// <summary>JSON-serialized string[] of dependency work item IDs.</summary>
    public string DependenciesJson { get; set; } = "[]";

    /// <summary>JSON-serialized array of <see cref="ConversationMessage"/>.</summary>
    public string ConversationJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized array of <see cref="WorkItemPullRequest"/> — every PR
    /// ever opened against this item, deduplicated by URL. Lives here rather
    /// than on the ILD instance's loop runs so it survives the run being
    /// reclaimed and the ILD instance being reset (WI-203).
    /// </summary>
    public string PullRequestsJson { get; set; } = "[]";

    [MaxLength(2048)]
    public string? HumanFeedbackActions { get; set; }

    /// <summary>
    /// Set on every successful poll heartbeat that includes this item's ID.
    /// Null means the item has never been seen by an ILD instance, which the
    /// stale detector treats as not-yet-claimed (non-stale).
    /// </summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>
    /// The LoopRun that created this work item (e.g. agent-created items).
    /// Stored on the server entity so it's available before any LoopRun exists
    /// for the work item itself.
    /// </summary>
    public Guid? CreatedByLoopRunId { get; set; }

    /// <summary>
    /// The Chat Session that created this work item (see ADR-0010). Mutually
    /// exclusive with <see cref="CreatedByLoopRunId"/>. Persists with a now-orphaned
    /// stamp after the chat is ended, exactly as run-created items keep
    /// <see cref="CreatedByLoopRunId"/> after their run is reclaimed.
    /// </summary>
    public Guid? CreatedByChatSessionId { get; set; }

    /// <summary>
    /// The repository this work item is associated with. Stored on the server
    /// so it round-trips and can be attached to LoopRun records on the client.
    /// </summary>
    public Guid? RepositoryId { get; set; }

    /// <summary>
    /// How this work item overrides the AI provider its AI nodes run against.
    /// Defaults to <see cref="AiProviderOverrideMode.None"/> (no override).
    /// </summary>
    public AiProviderOverrideMode AiProviderOverride { get; set; } = AiProviderOverrideMode.None;

    /// <summary>
    /// The AI provider (a client-side <c>AiProvider</c> id) an override targets.
    /// Only meaningful when <see cref="AiProviderOverride"/> is not
    /// <see cref="AiProviderOverrideMode.None"/>; stored opaquely like
    /// <see cref="RepositoryId"/> so it round-trips to the ILD instance.
    /// </summary>
    public Guid? AiProviderOverrideId { get; set; }

    /// <summary>
    /// The branch every run of this work item checks out, replacing the
    /// generated per-run name. Null or blank means the ILD instance generates
    /// <c>ild/wi-&lt;id&gt;-run-&lt;run&gt;</c> as before. Stored verbatim — no run
    /// suffix is appended — and capped at the length of the ILD instance's
    /// <c>LoopRun.BranchName</c> column so a name that round-trips here can
    /// always be persisted on the run. See ADR-0008.
    /// </summary>
    [MaxLength(256)]
    public string? BranchNameOverride { get; set; }

    /// <summary>
    /// The ref every run of this work item branches from — fetched, reset to,
    /// and rebased onto. Null or blank means the repository's default branch,
    /// which is the only base runs had before. Lets an item continue, review or
    /// hotfix a branch instead of always starting from the default. See
    /// ADR-0008; the clean-base invariant of ADR-0006 is unchanged, only which
    /// clean ref is used. Capped to match <see cref="BranchNameOverride"/>.
    /// </summary>
    [MaxLength(256)]
    public string? BaseBranchOverride { get; set; }
}
