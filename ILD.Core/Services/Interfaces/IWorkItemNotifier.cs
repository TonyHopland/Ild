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

    /// <summary>
    /// Signals that the PR badges a work item's board card shows (state, CI,
    /// review, mergeability) changed or were cleared. A hint only: listeners re-fetch.
    /// </summary>
    Task PrStatusChangedAsync(string workItemId);

    /// <summary>
    /// Signals that dropping a work item renumbered the whole board, keeping its
    /// order, so a board re-reads the columns it shows instead of one announcement
    /// per item. The dropped item itself is announced as a state change as well.
    /// </summary>
    Task WorkItemBoardReorderedAsync(string workItemId);

    /// <summary>
    /// Signals that a work item's edit proposals changed — one was made,
    /// approved, rejected or went stale — so an open detail view and the board
    /// card re-read them. A hint only: listeners re-fetch.
    /// </summary>
    Task WorkItemEditProposalsChangedAsync(string workItemId);

    /// <summary>
    /// Signals that suggestions for a global variable changed — one was made,
    /// decided, or went stale — so the Global Variables page, the work items'
    /// Action tabs and the menu badge re-read them. A hint only: listeners re-fetch.
    /// </summary>
    Task GlobalVariableProposalsChangedAsync(string name);

    /// <summary>
    /// Signals that a loop template's proposals changed — one was made, saved,
    /// rejected or went stale — so the Loop Editor, the work items' Action tabs
    /// and the template list's counts re-read them. A hint only: listeners re-fetch.
    /// </summary>
    Task LoopTemplateProposalsChangedAsync(Guid templateId);

    /// <summary>
    /// Signals that the human input suggestions for a work item changed — one was
    /// made, decided, or went stale — so its Action tab re-reads them. A hint only: listeners re-fetch.
    /// </summary>
    Task HumanInputProposalsChangedAsync(string workItemId);

    /// <summary>
    /// Signals that a work item was deleted, so boards drop its card. <paramref name="affectedIds"/>
    /// are the items that depended on it or that it depended on, whose dependency
    /// lists changed; listeners re-fetch them.
    /// </summary>
    Task WorkItemDeletedAsync(string workItemId, IReadOnlyList<string> affectedIds);
}

public sealed class NoopWorkItemNotifier : IWorkItemNotifier
{
    public Task WorkItemStateChangedAsync(string workItemId, RemoteWorkItemStatus oldStatus, RemoteWorkItemStatus newStatus) => Task.CompletedTask;
    public Task HumanFeedbackRequiredAsync(string workItemId, string reason) => Task.CompletedTask;
    public Task DependencyResolvedAsync(string workItemId) => Task.CompletedTask;
    public Task PreviewStateChangedAsync(string workItemId) => Task.CompletedTask;
    public Task SchedulerStateChangedAsync(bool isPaused, int maxConcurrent) => Task.CompletedTask;
    public Task RunProgressedAsync(string workItemId) => Task.CompletedTask;
    public Task PrStatusChangedAsync(string workItemId) => Task.CompletedTask;
    public Task WorkItemBoardReorderedAsync(string workItemId) => Task.CompletedTask;
    public Task WorkItemEditProposalsChangedAsync(string workItemId) => Task.CompletedTask;
    public Task GlobalVariableProposalsChangedAsync(string name) => Task.CompletedTask;
    public Task LoopTemplateProposalsChangedAsync(Guid templateId) => Task.CompletedTask;
    public Task HumanInputProposalsChangedAsync(string workItemId) => Task.CompletedTask;
    public Task WorkItemDeletedAsync(string workItemId, IReadOnlyList<string> affectedIds) => Task.CompletedTask;
}
