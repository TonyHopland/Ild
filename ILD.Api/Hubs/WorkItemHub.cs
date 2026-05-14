using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Hubs;

public class WorkItemHub : Hub
{
    private const string WorkItemGroup = "work-items";

    public async Task SubscribeToWorkItems()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, WorkItemGroup);
    }

    public async Task UnsubscribeFromWorkItems()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkItemGroup);
    }

    public async Task NotifyWorkItemStateChanged(string workItemId, WorkItemStatus oldStatus, WorkItemStatus newStatus)
    {
        await Clients.Group(WorkItemGroup).SendAsync("WorkItemStateChanged",
            new ILD.Data.DTOs.SignalRPayloads.WorkItemStateChangedPayload(workItemId, oldStatus, newStatus));
    }

    public async Task NotifyDependencyResolved(string workItemId)
    {
        await Clients.Group(WorkItemGroup).SendAsync("DependencyResolved",
            new ILD.Data.DTOs.SignalRPayloads.DependencyResolvedPayload(workItemId));
    }

    public async Task NotifyHumanFeedbackRequired(string workItemId, string reason)
    {
        await Clients.Group(WorkItemGroup).SendAsync("HumanFeedbackRequired",
            new ILD.Data.DTOs.SignalRPayloads.HumanFeedbackRequiredPayload(workItemId, reason));
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkItemGroup);
        await base.OnDisconnectedAsync(exception);
    }
}
