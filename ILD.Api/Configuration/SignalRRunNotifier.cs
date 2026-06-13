using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ILD.Api.Configuration;

public class SignalRRunNotifier : IRunNotifier
{
    private readonly IHubContext<LoopRunHub> _runHub;
    private readonly ILogger<SignalRRunNotifier> _logger;

    public SignalRRunNotifier(IHubContext<LoopRunHub> runHub, ILogger<SignalRRunNotifier> logger)
    {
        _runHub = runHub;
        _logger = logger;
    }

    public async Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus)
    {
        try
        {
            await _runHub.Clients.Group(runId.ToString())
                .SendAsync("NodeStateChanged", new NodeStateChangedPayload(runId, nodeId, oldStatus, newStatus));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send NodeStateChanged for run {RunId} node {NodeId}", runId, nodeId);
        }
    }

    public async Task EventLoggedAsync(Guid runId, string message, string eventType, Guid? nodeId, Guid? runNodeId)
    {
        try
        {
            await _runHub.Clients.Group(runId.ToString())
                .SendAsync("EventLogged", new EventLoggedPayload(runId, message, eventType, nodeId, runNodeId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send EventLogged for run {RunId}", runId);
        }
    }

    public async Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
    {
        try
        {
            await _runHub.Clients.Group(runId.ToString())
                .SendAsync("LoopRunStateChanged", new LoopRunStateChangedPayload(runId, oldStatus, newStatus));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send RunStateChanged for run {RunId}", runId);
        }
    }

    public async Task PausedAsync(Guid runId)
    {
        try
        {
            await _runHub.Clients.Group(runId.ToString())
                .SendAsync("Paused", new RunPausedPayload(runId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Paused for run {RunId}", runId);
        }
    }

    public async Task ResumedAsync(Guid runId)
    {
        try
        {
            await _runHub.Clients.Group(runId.ToString())
                .SendAsync("Resumed", new RunResumedPayload(runId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Resumed for run {RunId}", runId);
        }
    }

    public async Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq)
    {
        try
        {
            await _runHub.Clients.Group(runId.ToString())
                .SendAsync("NodeProgress", new NodeProgressPayload(runId, nodeId, line, seq));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send NodeProgress for run {RunId} node {NodeId}", runId, nodeId);
        }
    }
}
