using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

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

    /// <summary>
    /// Custom-edge key. Null for the default (<see cref="EdgeType.OnSuccess"/>)
    /// and fallback (<see cref="EdgeType.OnFailure"/>) edges; set to the edge
    /// name for <see cref="EdgeType.Custom"/> edges. The pair
    /// (<see cref="EdgeType"/>, <see cref="Name"/>) uniquely identifies an
    /// outgoing edge of a node.
    /// </summary>
    [MaxLength(256)]
    public string? Name { get; set; }

    public int? MaxTraversals { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(SourceNodeId))]
    public LoopNode SourceNode { get; set; } = null!;

    [ForeignKey(nameof(TargetNodeId))]
    public LoopNode TargetNode { get; set; } = null!;
}
