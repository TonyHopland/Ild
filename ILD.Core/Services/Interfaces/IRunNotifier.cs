using ILD.Data.Enums;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Sink for runtime notifications (in production: SignalR).
/// Implementations must be safe to call from any thread and never throw.
/// </summary>
public interface IRunNotifier
{
    Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus);
    Task EventLoggedAsync(Guid runId, string message, string eventType, Guid? nodeId, Guid? runNodeId);
    Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus);
    Task PausedAsync(Guid runId);
    Task ResumedAsync(Guid runId);
    Task HaltedAsync(Guid runId);
    Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq);

    /// <summary>The run's persisted PR snapshot was refreshed by the heartbeat poller.</summary>
    Task PrSnapshotChangedAsync(Guid runId);

    /// <summary>
    /// What the run intends to write on its pull request changed — an agent
    /// queued an answer, a person dropped one, or the PR node claimed the lot to
    /// send it. The window in which a person can still drop something opens
    /// while the round is mid-flight, so it cannot wait for the next snapshot.
    /// </summary>
    Task PrQueueChangedAsync(Guid runId);
}

public sealed class NoopRunNotifier : IRunNotifier
{
    public Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus) => Task.CompletedTask;
    public Task EventLoggedAsync(Guid runId, string message, string eventType, Guid? nodeId, Guid? runNodeId) => Task.CompletedTask;
    public Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus) => Task.CompletedTask;
    public Task PausedAsync(Guid runId) => Task.CompletedTask;
    public Task ResumedAsync(Guid runId) => Task.CompletedTask;
    public Task HaltedAsync(Guid runId) => Task.CompletedTask;
    public Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq) => Task.CompletedTask;
    public Task PrSnapshotChangedAsync(Guid runId) => Task.CompletedTask;
    public Task PrQueueChangedAsync(Guid runId) => Task.CompletedTask;
}
