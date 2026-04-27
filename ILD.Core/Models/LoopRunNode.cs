using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Core.Enums;

namespace ILD.Core.Models;

public class LoopRunNode
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey("LoopRun")]
    public Guid LoopRunId { get; set; }

    [Required]
    [ForeignKey("LoopNode")]
    public Guid LoopNodeId { get; set; }

    public LoopRunNodeStatus Status { get; set; }

    public string? Output { get; set; }

    public int RetryCount { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun LoopRun { get; set; } = null!;

    [ForeignKey(nameof(LoopNodeId))]
    public LoopNode LoopNode { get; set; } = null!;
}
