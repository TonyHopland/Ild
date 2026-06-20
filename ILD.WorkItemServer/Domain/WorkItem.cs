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
}
