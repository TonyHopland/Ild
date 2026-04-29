using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

public class LoopTemplateVersion
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("LoopTemplate")]
    public Guid LoopTemplateId { get; set; }

    public int VersionNumber { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(LoopTemplateId))]
    public LoopTemplate LoopTemplate { get; set; } = null!;

    [InverseProperty("LoopTemplateVersion")]
    public ICollection<LoopNode> Nodes { get; set; } = new List<LoopNode>();

    [InverseProperty("LoopTemplateVersion")]
    public ICollection<WorkItem> WorkItems { get; set; } = new List<WorkItem>();

    [InverseProperty("LoopTemplateVersion")]
    public ICollection<LoopRun> LoopRuns { get; set; } = new List<LoopRun>();
}
