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

    public string? EffectiveInput { get; set; }

    // Previous LoopRunNode in this run; nullable for the entry node.
    public Guid? PreviousNodeId { get; set; }

    // The LoopNodeEdge the engine traversed to enter this node visit; null
    // for the entry visit. Used to rebuild per-edge traversal counts after
    // server restart so the edge-traversal safety net stays accurate across
    // recovery boundaries.
    public Guid? IncomingEdgeId { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    // AI token/cost accounting captured from the agent CLI's own usage
    // reporting. Null for non-AI nodes and for AI turns where the provider
    // reported no usage. CostUsd is null when the CLI reports no monetary cost
    // (e.g. subscription-auth providers). Aggregated per run/template by the
    // analytics dashboard.
    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public decimal? CostUsd { get; set; }

    // The AI provider (by configured name) that executed this node, stamped on
    // every successful AI node so the analytics dashboard can attribute
    // tokens/cost per provider. Null for non-AI nodes.
    public string? AiProvider { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun LoopRun { get; set; } = null!;

    [ForeignKey(nameof(LoopNodeId))]
    public LoopNode LoopNode { get; set; } = null!;
}
