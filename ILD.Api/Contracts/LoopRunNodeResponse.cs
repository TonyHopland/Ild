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
    int ExecutionCount)
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
        0);
}
