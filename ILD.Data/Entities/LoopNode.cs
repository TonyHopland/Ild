using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class LoopNode
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("LoopTemplateVersion")]
    public Guid LoopTemplateVersionId { get; set; }

    public NodeType NodeType { get; set; }

    [Required]
    [MaxLength(256)]
    public string Label { get; set; } = string.Empty;

    public string? Config { get; set; }

    public int MaxRetries { get; set; } = 0;

    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(LoopTemplateVersionId))]
    public LoopTemplateVersion LoopTemplateVersion { get; set; } = null!;

    [InverseProperty("SourceNode")]
    public ICollection<LoopNodeEdge> OutgoingEdges { get; set; } = new List<LoopNodeEdge>();

    [InverseProperty("TargetNode")]
    public ICollection<LoopNodeEdge> IncomingEdges { get; set; } = new List<LoopNodeEdge>();

    [InverseProperty("LoopNode")]
    public ICollection<LoopRunNode> RunNodes { get; set; } = new List<LoopRunNode>();
}
