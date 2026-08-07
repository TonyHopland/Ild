using ILD.Api.Authentication;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Hubs;

/// <summary>
/// Broadcasts backlog-wide work item events — state changes, dependency
/// resolutions, human-feedback requests — to every client watching the backlog.
///
/// <para>There is deliberately no ownership check on
/// <see cref="SubscribeToWorkItems"/>. Two reasons, either sufficient: the hub
/// has a single global group rather than per-item groups, so there is no
/// per-item subscription to scope; and a work item has no owner to scope by —
/// <c>CreatedBy</c> and <c>CreatedByChatSessionId</c> record which principal or
/// chat produced the item (provenance), not who may see it, and work items live
/// on the standalone Work Item Server (ADR-0001) where the backlog is shared.
/// Introducing ownership would be a product decision, not a hardening one. This
/// paragraph exists so the next reader knows the question was asked and answered
/// rather than overlooked.</para>
///
/// <para>The attribute is deliberately redundant with the user-only fallback
/// policy that already covers this hub: it states at the class what the pipeline
/// states globally. It names <see cref="IldAuthentication.UserOnlyPolicy"/> and
/// not a bare <c>[Authorize]</c> for the reason documented there — a bare one
/// would suppress the fallback and leave the hub open to the agent token.</para>
/// </summary>
[Authorize(Policy = IldAuthentication.UserOnlyPolicy)]
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
