using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Core.Enums;

namespace ILD.Core.Models;

public class LoopNodeEdge
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("SourceNode")]
    public Guid SourceNodeId { get; set; }

    [Required]
    [ForeignKey("TargetNode")]
    public Guid TargetNodeId { get; set; }

    public EdgeType EdgeType { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(SourceNodeId))]
    public LoopNode SourceNode { get; set; } = null!;

    [ForeignKey(nameof(TargetNodeId))]
    public LoopNode TargetNode { get; set; } = null!;

    [InverseProperty("Edge")]
    public ICollection<LoopRunEdgeTraversal> Traversals { get; set; } = new List<LoopRunEdgeTraversal>();
}
