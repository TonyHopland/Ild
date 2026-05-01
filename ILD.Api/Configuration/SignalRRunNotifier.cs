using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Configuration;

public class SignalRRunNotifier : IRunNotifier
{
    private readonly IHubContext<LoopRunHub> _runHub;

    public SignalRRunNotifier(IHubContext<LoopRunHub> runHub)
    {
        _runHub = runHub;
    }

    public Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus)
        => _runHub.Clients.Group(runId.ToString())
            .SendAsync("NodeStateChanged", new NodeStateChangedPayload(runId, nodeId, oldStatus, newStatus));

    public Task EventLoggedAsync(Guid runId, string message)
        => _runHub.Clients.Group(runId.ToString())
            .SendAsync("EventLogged", new EventLoggedPayload(runId, message));

    public Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
        => _runHub.Clients.Group(runId.ToString())
            .SendAsync("LoopRunStateChanged", new LoopRunStateChangedPayload(runId, oldStatus, newStatus));

    public Task PausedAsync(Guid runId)
        => _runHub.Clients.Group(runId.ToString())
            .SendAsync("Paused", new RunPausedPayload(runId));

    public Task ResumedAsync(Guid runId)
        => _runHub.Clients.Group(runId.ToString())
            .SendAsync("Resumed", new RunResumedPayload(runId));
}
