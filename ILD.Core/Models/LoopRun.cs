using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Core.Enums;

namespace ILD.Core.Models;

public class LoopRun
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("WorkItem")]
    public Guid WorkItemId { get; set; }

    [Required]
    [ForeignKey("LoopTemplateVersion")]
    public Guid LoopTemplateVersionId { get; set; }

    public LoopRunStatus Status { get; set; }

    [Required]
    [MaxLength(128)]
    public string RecoveryPolicy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(WorkItemId))]
    public WorkItem WorkItem { get; set; } = null!;

    [ForeignKey(nameof(LoopTemplateVersionId))]
    public LoopTemplateVersion LoopTemplateVersion { get; set; } = null!;

    [InverseProperty("LoopRun")]
    public ICollection<LoopRunNode> RunNodes { get; set; } = new List<LoopRunNode>();

    [InverseProperty("LoopRun")]
    public ICollection<LoopRunEdgeTraversal> EdgeTraversals { get; set; } = new List<LoopRunEdgeTraversal>();

    [InverseProperty("LoopRun")]
    public ICollection<EventLog> EventLogs { get; set; } = new List<EventLog>();
}
