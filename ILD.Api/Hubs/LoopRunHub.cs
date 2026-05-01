using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Hubs;

public class LoopRunHub : Hub
{
    public async Task SubscribeToRun(Guid runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, runId.ToString());
    }

    public async Task UnsubscribeFromRun(Guid runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, runId.ToString());
    }

    public async Task NotifyNodeStateChanged(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus)
    {
        await Clients.Group(runId.ToString()).SendAsync("NodeStateChanged",
            new ILD.Data.DTOs.SignalRPayloads.NodeStateChangedPayload(runId, nodeId, oldStatus, newStatus));
    }

    public async Task NotifyEventLogged(Guid runId, string eventMessage)
    {
        await Clients.Group(runId.ToString()).SendAsync("EventLogged",
            new ILD.Data.DTOs.SignalRPayloads.EventLoggedPayload(runId, eventMessage));
    }

    public async Task NotifyLoopRunStateChanged(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
    {
        await Clients.Group(runId.ToString()).SendAsync("LoopRunStateChanged",
            new ILD.Data.DTOs.SignalRPayloads.LoopRunStateChangedPayload(runId, oldStatus, newStatus));
    }

    public async Task NotifyPaused(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("Paused",
            new ILD.Data.DTOs.SignalRPayloads.RunPausedPayload(runId));
    }

    public async Task NotifyResumed(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("Resumed",
            new ILD.Data.DTOs.SignalRPayloads.RunResumedPayload(runId));
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
