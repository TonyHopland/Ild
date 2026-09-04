using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ILD.Tests;

public class SignalRLogNotifierTests
{
    private static (SignalRLogNotifier Notifier, List<object?[]> Sent) Build(Exception? failWith = null)
    {
        var sent = new List<object?[]>();
        var proxy = new Mock<IClientProxy>();
        var send = proxy.Setup(p => p.SendCoreAsync("LogEntryAppended", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => sent.Add(args));
        if (failWith is null) send.Returns(Task.CompletedTask);
        else send.ThrowsAsync(failWith);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group("work-items")).Returns(proxy.Object);
        var hub = new Mock<IHubContext<WorkItemHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        return (new SignalRLogNotifier(hub.Object), sent);
    }

    private static LogEntryPayload Entry(long id = 1) =>
        new(id, DateTimeOffset.UtcNow, "Warning", "ILD.Core.LoopEngine", "Run 7f21 stalled", null);

    [Fact]
    public async Task Each_entry_is_pushed_once_to_the_work_item_group()
    {
        var (notifier, sent) = Build();

        await notifier.LogEntryAppendedAsync(Entry(1));
        await notifier.LogEntryAppendedAsync(Entry(2));

        Assert.Equal(2, sent.Count);
        var payload = Assert.IsType<LogEntryPayload>(Assert.Single(sent[0]));
        Assert.Equal("Warning", payload.Level);
        Assert.Equal("Run 7f21 stalled", payload.Message);
    }

    /// <summary>
    /// The push is what a log line triggers, so a failed push must not write one:
    /// it would be buffered, pushed, and fail again. Taking no logger at all is
    /// what makes that true; this pins the other half, that the failure stays in.
    /// </summary>
    [Fact]
    public async Task A_failed_push_is_swallowed_rather_than_reported()
    {
        var (notifier, _) = Build(failWith: new InvalidOperationException("no connection"));

        Assert.Null(await Record.ExceptionAsync(() => notifier.LogEntryAppendedAsync(Entry())));
    }
}
