using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Hubs;

public class LoopRunHub : Hub
{
    private readonly IRunProgressBuffer _progressBuffer;

    public LoopRunHub(IRunProgressBuffer progressBuffer)
    {
        _progressBuffer = progressBuffer;
    }

    /// <summary>
    /// Join the run's group and return the live-output captured so far so the
    /// caller can render the run from its start before attaching to the live
    /// stream. The returned <see cref="RunProgressSnapshot.LastSeq"/> lets the
    /// caller drop any live chunk already contained in the replay.
    /// </summary>
    public async Task<RunProgressSnapshot> SubscribeToRun(Guid runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, runId.ToString());
        return _progressBuffer.Snapshot(runId);
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

    public async Task NotifyEventLogged(Guid runId, string eventMessage, string eventType, Guid? nodeId, Guid? runNodeId)
    {
        await Clients.Group(runId.ToString()).SendAsync("EventLogged",
            new ILD.Data.DTOs.SignalRPayloads.EventLoggedPayload(runId, eventMessage, eventType, nodeId, runNodeId));
    }

    public async Task NotifyLoopRunStateChanged(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
    {
        await Clients.Group(runId.ToString()).SendAsync("LoopRunStateChanged",
            new ILD.Data.DTOs.SignalRPayloads.LoopRunStateChangedPayload(runId, oldStatus, newStatus));
    }

    public async Task NotifyPaused(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("RunPaused",
            new ILD.Data.DTOs.SignalRPayloads.RunPausedPayload(runId));
    }

    public async Task NotifyResumed(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("RunResumed",
            new ILD.Data.DTOs.SignalRPayloads.RunResumedPayload(runId));
    }

    public async Task NotifyHalted(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("RunHalted",
            new ILD.Data.DTOs.SignalRPayloads.RunHaltedPayload(runId));
    }

    public async Task NotifyNodeProgress(Guid runId, Guid nodeId, string line, long seq)
    {
        await Clients.Group(runId.ToString()).SendAsync("NodeProgress",
            new ILD.Data.DTOs.SignalRPayloads.NodeProgressPayload(runId, nodeId, line, seq));
    }

    public async Task NotifyPrSnapshotChanged(Guid runId)
    {
        await Clients.Group(runId.ToString()).SendAsync("PrSnapshotChanged",
            new ILD.Data.DTOs.SignalRPayloads.PrSnapshotChangedPayload(runId));
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
