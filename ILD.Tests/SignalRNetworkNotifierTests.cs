using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class SignalRNetworkNotifierTests
{
    /// <summary>
    /// The hub serialises enums as numbers while the REST API sends their names,
    /// and the Settings page lower-cases the decision to pick a colour. A live log
    /// line therefore has to carry the name, or the page throws on the first
    /// connection the agent makes.
    /// </summary>
    [Fact]
    public async Task A_live_log_line_carries_the_decision_by_name()
    {
        var proxy = new Mock<IClientProxy>();
        object?[]? sent = null;
        proxy.Setup(p => p.SendCoreAsync("NetworkLogAppended", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => sent = args)
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group("work-items")).Returns(proxy.Object);
        var hub = new Mock<IHubContext<WorkItemHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        var notifier = new SignalRNetworkNotifier(hub.Object, NullLogger<SignalRNetworkNotifier>.Instance);

        await notifier.LogEntryAppendedAsync(new NetworkLogEntry
        {
            Id = Guid.NewGuid(), Host = "api.example.com", Port = 443, Timestamp = DateTime.UtcNow, Decision = NetworkDecision.Blocked,
        });

        var payload = Assert.IsType<NetworkLogAppendedPayload>(Assert.Single(sent!));
        Assert.Equal("Blocked", payload.Decision);
        Assert.Equal("api.example.com", payload.Host);
    }
}
