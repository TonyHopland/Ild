using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using System.Collections.Concurrent;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Hubs;

public class LoopRunHub : Hub
{
    private static readonly ConcurrentDictionary<Guid, HashSet<string>> _runGroups = new();

    public async Task SubscribeToRun(Guid runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, runId.ToString());
        _runGroups.TryGetValue(runId, out var connections);
        if (connections != null)
        {
            connections.Add(Context.ConnectionId);
        }
    }

    public async Task UnsubscribeFromRun(Guid runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, runId.ToString());
        _runGroups.TryGetValue(runId, out var connections);
        if (connections != null)
        {
            connections.Remove(Context.ConnectionId);
        }
    }

    public async Task NotifyNodeStateChanged(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus)
    {
        await Clients.Group(runId.ToString()).SendAsync("NodeStateChanged", runId, nodeId, oldStatus, newStatus);
    }

    public async Task NotifyEventLogged(Guid runId, string eventMessage)
    {
        await Clients.Group(runId.ToString()).SendAsync("EventLogged", runId, eventMessage);
    }

    public async Task NotifyLoopRunStateChanged(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
    {
        await Clients.Group(runId.ToString()).SendAsync("LoopRunStateChanged", runId, oldStatus, newStatus);
    }

    public async Task NotifyPaused(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("Paused", runId);
    }

    public async Task NotifyResumed(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("Resumed", runId);
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var runId in _runGroups.Keys.ToList())
        {
            _runGroups.TryGetValue(runId, out var connections);
            connections?.Remove(Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
