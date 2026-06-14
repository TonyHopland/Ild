using ILD.Api.Hubs;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Configuration;

public class SignalRWorkItemNotifier : IWorkItemNotifier
{
    private const string WorkItemGroup = "work-items";
    private readonly IHubContext<WorkItemHub> _hub;

    public SignalRWorkItemNotifier(IHubContext<WorkItemHub> hub)
    {
        _hub = hub;
    }

    public Task WorkItemStateChangedAsync(string workItemId, RemoteWorkItemStatus oldStatus, RemoteWorkItemStatus newStatus)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("WorkItemStateChanged", new WorkItemStateChangedPayload(workItemId, (WorkItemStatus)(int)oldStatus, (WorkItemStatus)(int)newStatus));

    public Task HumanFeedbackRequiredAsync(string workItemId, string reason)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("HumanFeedbackRequired", new HumanFeedbackRequiredPayload(workItemId, reason));

    public Task DependencyResolvedAsync(string workItemId)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("DependencyResolved", new DependencyResolvedPayload(workItemId));

    public Task PreviewStateChangedAsync(string workItemId)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("PreviewStateChanged", new PreviewStateChangedPayload(workItemId));

    public Task RunProgressedAsync(string workItemId)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("WorkItemRunProgressed", new WorkItemRunProgressedPayload(workItemId));

    public Task SchedulerStateChangedAsync(bool isPaused, int maxConcurrent)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("SchedulerStateChanged", new SchedulerStateChangedPayload(isPaused, maxConcurrent));
}
