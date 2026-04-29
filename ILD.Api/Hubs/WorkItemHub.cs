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

    public async Task NotifyWorkItemStateChanged(Guid workItemId, WorkItemStatus oldStatus, WorkItemStatus newStatus)
    {
        await Clients.Group(WorkItemGroup).SendAsync("WorkItemStateChanged", workItemId, oldStatus, newStatus);
    }

    public async Task NotifyDependencyResolved(Guid workItemId)
    {
        await Clients.Group(WorkItemGroup).SendAsync("DependencyResolved", workItemId);
    }

    public async Task NotifyHumanFeedbackRequired(Guid workItemId, string reason)
    {
        await Clients.Group(WorkItemGroup).SendAsync("HumanFeedbackRequired", workItemId, reason);
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
