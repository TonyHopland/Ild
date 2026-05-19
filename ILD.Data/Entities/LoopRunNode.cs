using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

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

    public string? NodeLabel { get; set; }

    public LoopRunNodeStatus Status { get; set; }

    public string? Output { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// JSON describing the effective input the node ran with, after template
    /// transformation (e.g. resolved prompt with placeholders substituted).
    /// Set by the engine before execution from <c>DescribeInput</c> and updated
    /// after execution with resolved data from the outcome.
    /// </summary>
    public string? EffectiveInput { get; set; }

    public int RetryCount { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun LoopRun { get; set; } = null!;

    [ForeignKey(nameof(LoopNodeId))]
    public LoopNode LoopNode { get; set; } = null!;
}
