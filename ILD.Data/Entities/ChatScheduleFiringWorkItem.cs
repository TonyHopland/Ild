using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A work item a firing's own turn asked the WorkItem server to create. Written
/// just before the server is asked, so no create goes unrecorded: until the
/// server's answer names the item, <see cref="WorkItemId"/> is null and the
/// record counts as unresolved.
/// </summary>
public class ChatScheduleFiringWorkItem
{
    [Key]
    public Guid Id { get; set; }

    public Guid ChatScheduleFiringId { get; set; }

    [MaxLength(64)]
    public string? WorkItemId { get; set; }

    public DateTime CreatedAt { get; set; }
}
