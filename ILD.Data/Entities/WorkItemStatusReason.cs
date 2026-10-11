using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// Why a work item is waiting on a person when no run is there to say so: a
/// start that failed before its run existed, or a manual move to HumanFeedback
/// with no live run. Ild-only data, so it lives here keyed by work item id
/// rather than on the WorkItem server (ADR-0025). Cleared when a run starts.
/// </summary>
public class WorkItemStatusReason
{
    [Key]
    public string WorkItemId { get; set; } = string.Empty;

    [Required]
    public string Text { get; set; } = string.Empty;

    public DateTime At { get; set; }
}
