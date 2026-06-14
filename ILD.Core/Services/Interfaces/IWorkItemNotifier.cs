using ILD.Data.Enums;
using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Sink for work item lifecycle notifications (in production: SignalR).
/// Implementations must be safe to call from any thread and never throw.
/// </summary>
public interface IWorkItemNotifier
{
    Task WorkItemStateChangedAsync(string workItemId, RemoteWorkItemStatus oldStatus, RemoteWorkItemStatus newStatus);
    Task HumanFeedbackRequiredAsync(string workItemId, string reason);
    Task DependencyResolvedAsync(string workItemId);
    Task PreviewStateChangedAsync(string workItemId);
    Task SchedulerStateChangedAsync(bool isPaused, int maxConcurrent);

    /// <summary>
    /// Signals that a work item's active run advanced to a new node, so its
    /// taskboard card can refresh the step it is on without the work item's
    /// status changing. Carries no node detail — listeners re-fetch the item.
    /// </summary>
    Task RunProgressedAsync(string workItemId);
}

public sealed class NoopWorkItemNotifier : IWorkItemNotifier
{
    public Task WorkItemStateChangedAsync(string workItemId, RemoteWorkItemStatus oldStatus, RemoteWorkItemStatus newStatus) => Task.CompletedTask;
    public Task HumanFeedbackRequiredAsync(string workItemId, string reason) => Task.CompletedTask;
    public Task DependencyResolvedAsync(string workItemId) => Task.CompletedTask;
    public Task PreviewStateChangedAsync(string workItemId) => Task.CompletedTask;
    public Task SchedulerStateChangedAsync(bool isPaused, int maxConcurrent) => Task.CompletedTask;
    public Task RunProgressedAsync(string workItemId) => Task.CompletedTask;
}
