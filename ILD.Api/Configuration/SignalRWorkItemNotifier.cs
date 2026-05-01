using ILD.Api.Hubs;
using ILD.Core.Services.Interfaces;
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

    public Task WorkItemStateChangedAsync(Guid workItemId, WorkItemStatus oldStatus, WorkItemStatus newStatus)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("WorkItemStateChanged", new WorkItemStateChangedPayload(workItemId, oldStatus, newStatus));

    public Task HumanFeedbackRequiredAsync(Guid workItemId, string reason)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("HumanFeedbackRequired", new HumanFeedbackRequiredPayload(workItemId, reason));

    public Task DependencyResolvedAsync(Guid workItemId)
        => _hub.Clients.Group(WorkItemGroup)
            .SendAsync("DependencyResolved", new DependencyResolvedPayload(workItemId));
}
