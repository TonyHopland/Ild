using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

public class WorkItemDependency
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("WorkItem")]
    public Guid WorkItemId { get; set; }

    [Required]
    [ForeignKey("DependentWorkItem")]
    public Guid DependencyWorkItemId { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(WorkItemId))]
    public WorkItem WorkItem { get; set; } = null!;

    [ForeignKey(nameof(DependencyWorkItemId))]
    public WorkItem DependentWorkItem { get; set; } = null!;
}
