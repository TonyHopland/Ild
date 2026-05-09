using ILD.Data.Enums;
using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Sink for work item lifecycle notifications (in production: SignalR).
/// Implementations must be safe to call from any thread and never throw.
/// </summary>
public interface IWorkItemNotifier
{
    Task WorkItemStateChangedAsync(Guid workItemId, RemoteWorkItemStatus oldStatus, RemoteWorkItemStatus newStatus);
    Task HumanFeedbackRequiredAsync(Guid workItemId, string reason);
    Task DependencyResolvedAsync(Guid workItemId);
}

public sealed class NoopWorkItemNotifier : IWorkItemNotifier
{
    public Task WorkItemStateChangedAsync(Guid workItemId, RemoteWorkItemStatus oldStatus, RemoteWorkItemStatus newStatus) => Task.CompletedTask;
    public Task HumanFeedbackRequiredAsync(Guid workItemId, string reason) => Task.CompletedTask;
    public Task DependencyResolvedAsync(Guid workItemId) => Task.CompletedTask;
}
