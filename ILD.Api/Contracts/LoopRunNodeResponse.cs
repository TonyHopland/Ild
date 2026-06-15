using ILD.Data.Entities;

namespace ILD.Api.Contracts;

/// <summary>
/// API projection of a <see cref="LoopRunNode"/>. Shared by the run-list and
/// run-detail endpoints so the node shape (and the not-yet-implemented
/// <see cref="ExecutionCount"/> placeholder) is defined in exactly one place.
/// </summary>
public sealed record LoopRunNodeResponse(
    Guid Id,
    Guid NodeId,
    string NodeLabel,
    string Status,
    string? EffectiveInput,
    string? Output,
    string? Error,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int ExecutionCount,
    // The template node's type (e.g. "AI"), when the LoopNode navigation was
    // eager-loaded. Null on the list endpoint, which doesn't load it. Drives
    // the live-view Halt affordance, which is AI-node only.
    string? NodeType,
    // AI token/cost accounting captured from the agent CLI; null for non-AI
    // nodes or turns where the provider reported no usage.
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd)
{
    public static LoopRunNodeResponse From(LoopRunNode rn) => new(
        rn.Id,
        rn.LoopNodeId,
        rn.NodeLabel ?? rn.LoopNode?.Label ?? string.Empty,
        rn.Status.ToString(),
        rn.EffectiveInput,
        rn.Output,
        rn.Error,
        rn.StartedAt,
        rn.CompletedAt,
        0,
        rn.LoopNode?.NodeType.ToString(),
        rn.InputTokens,
        rn.OutputTokens,
        rn.CostUsd);
}
