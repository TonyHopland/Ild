using ILD.Api.Hubs;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ILD.Api.Configuration;

/// <summary>
/// Egress-filter events for the Settings page, sent on the work-item hub's group
/// like <c>SchedulerStateChanged</c> — settings are instance-wide, and that is the
/// hub every signed-in page already holds open.
/// </summary>
public class SignalRNetworkNotifier : INetworkNotifier
{
    private const string WorkItemGroup = "work-items";
    private readonly IHubContext<WorkItemHub> _hub;
    private readonly ILogger<SignalRNetworkNotifier> _logger;

    public SignalRNetworkNotifier(IHubContext<WorkItemHub> hub, ILogger<SignalRNetworkNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public Task PolicyChangedAsync()
        => Send("NetworkPolicyChanged", new NetworkPolicyChangedPayload());

    public Task LogEntryAppendedAsync(NetworkLogEntry entry)
        => Send("NetworkLogAppended", new NetworkLogAppendedPayload(
            entry.Id, entry.Host, entry.Port, entry.Timestamp, entry.Decision, entry.AiProviderId));

    public Task LogClearedAsync()
        => Send("NetworkLogCleared", new NetworkLogClearedPayload());

    private async Task Send(string method, object payload)
    {
        try
        {
            await _hub.Clients.Group(WorkItemGroup).SendAsync(method, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {Method}", method);
        }
    }
}
