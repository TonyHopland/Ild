using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Configuration;

/// <summary>
/// Sends a buffered log line to the Logging settings page, on the work-item
/// hub's group like <see cref="SignalRNetworkNotifier"/>. Wired to
/// <see cref="LogEntryBuffer.Appended"/> once the container is built.
/// </summary>
public sealed class SignalRLogNotifier
{
    private const string WorkItemGroup = "work-items";
    private readonly IHubContext<WorkItemHub> _hub;

    public SignalRLogNotifier(IHubContext<WorkItemHub> hub)
    {
        _hub = hub;
    }

    /// <remarks>
    /// The one notifier that says nothing when it fails: a log line about a
    /// failed push is a log line, which is buffered, pushed, and fails again.
    /// </remarks>
    public async Task LogEntryAppendedAsync(LogEntryPayload entry)
    {
        try
        {
            await _hub.Clients.Group(WorkItemGroup).SendAsync("LogEntryAppended", entry);
        }
        catch
        {
        }
    }
}
