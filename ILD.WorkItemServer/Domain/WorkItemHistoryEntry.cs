using System.ComponentModel.DataAnnotations;

namespace ILD.WorkItemServer.Domain;

public enum WorkItemHistoryKind
{
    /// <summary>The item's values as it was created.</summary>
    Created = 0,

    /// <summary>
    /// The values an item already had when history began, seeded by the
    /// migration that added it. Not its original values: what came before is unknown.
    /// </summary>
    Baseline = 1,

    Changed = 2,
}

public enum WorkItemActorKind
{
    Human = 0,
    Agent = 1,
    Chat = 2,
    System = 3,
}

/// <summary>
/// One field's new value in one save of a work item. Append-only: a row is never
/// updated or deleted, not even with its work item, and only the new value is
/// stored — the old one is the value of the previous row for the same item and
/// field. That holds only while every write to a tracked field leaves a row, which
/// is why every item starts with a Created or Baseline save.
/// </summary>
public class WorkItemHistoryEntry
{
    [Key]
    public long Id { get; set; }

    /// <summary>The item's textual id. No foreign key: history outlives the item.</summary>
    [Required]
    [MaxLength(64)]
    public string WorkItemId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    /// <summary>Shared by every row one save wrote, so the save reads as one event.</summary>
    [Required]
    [MaxLength(64)]
    public string SaveId { get; set; } = string.Empty;

    public WorkItemHistoryKind Kind { get; set; }

    /// <summary>Who acted. Null only on a Baseline whose item never recorded its creator.</summary>
    [MaxLength(256)]
    public string? Username { get; set; }

    public WorkItemActorKind? ActorKind { get; set; }

    [MaxLength(1024)]
    public string? ActorDetail { get; set; }

    /// <summary>The work item the acting loop run belongs to, when the actor is one.</summary>
    [MaxLength(64)]
    public string? ActorWorkItemId { get; set; }

    [Required]
    [MaxLength(64)]
    public string Field { get; set; } = string.Empty;

    public string? Value { get; set; }

    [MaxLength(WorkItemHistoryFields.MaxReasonLength)]
    public string? Reason { get; set; }
}
