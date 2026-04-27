using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Core.Models;

public class LoopRunEdgeTraversal
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("LoopRun")]
    public Guid LoopRunId { get; set; }

    [Required]
    [ForeignKey("Edge")]
    public Guid EdgeId { get; set; }

    public int TraversalCount { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun LoopRun { get; set; } = null!;

    [ForeignKey(nameof(EdgeId))]
    public LoopNodeEdge Edge { get; set; } = null!;
}
