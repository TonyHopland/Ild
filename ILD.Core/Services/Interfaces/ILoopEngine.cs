using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Result of an external event for a node parked in <c>WaitingHuman</c>.
/// </summary>
public sealed record NodeSignal(bool Success, string? Output = null, string? Error = null)
{
    public static NodeSignal Succeeded(string? output = null) => new(true, output);
    public static NodeSignal Failed(string error, string? output = null) => new(false, output, error);
}

public interface ILoopEngine
{
    Task StartRunAsync(Guid workItemId, CancellationToken cancellationToken = default);
    Task PauseRunAsync(Guid runId);
    Task ResumeRunAsync(Guid runId);
    Task CancelRunAsync(Guid runId);
    Task<LoopRunStatus?> GetRunStatusAsync(Guid runId);
    Task<IEnumerable<Guid>> GetActiveRunIdsAsync();

    /// <summary>
    /// Deliver the outcome of an externally-signalled node (PR webhook,
    /// human-feedback API, scheduled timer, etc.) and re-enter the run loop.
    /// </summary>
    Task SignalNodeResultAsync(Guid runId, Guid runNodeId, NodeSignal signal);

    Task ResumeRecoveredRunAsync(Guid runId);
}

