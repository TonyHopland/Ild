using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class SignalRNotifierTests
{
    private sealed record Row(string Event, string Group, Type? PayloadType, Func<IHubClients, Task> Send, Action<object?> Verify);

    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid NodeId = Guid.NewGuid();
    private static readonly Guid RunNodeId = Guid.NewGuid();
    private static readonly Guid ChatSessionId = Guid.NewGuid();
    private static readonly string WorkItemId = Guid.NewGuid().ToString();
    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Func<IHubClients, Task> OnRun(Func<SignalRRunNotifier, Task> send) => clients =>
    {
        var ctx = new Mock<IHubContext<LoopRunHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients);
        return send(new SignalRRunNotifier(ctx.Object, NullLogger<SignalRRunNotifier>.Instance));
    };

    private static Func<IHubClients, Task> OnWorkItem(Func<SignalRWorkItemNotifier, Task> send) => clients =>
    {
        var ctx = new Mock<IHubContext<WorkItemHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients);
        return send(new SignalRWorkItemNotifier(ctx.Object));
    };

    private static Func<IHubClients, Task> OnLog(Func<SignalRLogNotifier, Task> send) => clients =>
    {
        var ctx = new Mock<IHubContext<WorkItemHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients);
        return send(new SignalRLogNotifier(ctx.Object));
    };

    private static Func<IHubClients, Task> OnChat(Func<SignalRChatNotifier, Task> send) => clients =>
    {
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients);
        return send(new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance));
    };

    private static System.Text.Json.JsonElement AsJson(object? payload) =>
        System.Text.Json.JsonSerializer.SerializeToElement(
            payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private static readonly Dictionary<string, Row> Rows = new Row[]
    {
        new("NodeStateChanged", RunId.ToString(), typeof(NodeStateChangedPayload),
            OnRun(n => n.NodeStateChangedAsync(RunId, NodeId, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running)),
            p =>
            {
                var payload = (NodeStateChangedPayload)p!;
                Assert.Equal(RunId, payload.RunId);
                Assert.Equal(NodeId, payload.NodeId);
                Assert.Equal(LoopRunNodeStatus.Pending, payload.OldStatus);
                Assert.Equal(LoopRunNodeStatus.Running, payload.NewStatus);
            }),
        new("LoopRunStateChanged", RunId.ToString(), typeof(LoopRunStateChangedPayload),
            OnRun(n => n.RunStateChangedAsync(RunId, LoopRunStatus.Running, LoopRunStatus.Completed)),
            p =>
            {
                var payload = (LoopRunStateChangedPayload)p!;
                Assert.Equal(RunId, payload.RunId);
                Assert.Equal(LoopRunStatus.Completed, payload.NewStatus);
            }),
        new("EventLogged", RunId.ToString(), typeof(EventLoggedPayload),
            OnRun(n => n.EventLoggedAsync(RunId, 42, "NodeStarted", NodeId, RunNodeId, At)),
            p =>
            {
                var payload = (EventLoggedPayload)p!;
                Assert.Equal(RunId, payload.RunId);
                Assert.Equal(42, payload.Id);
                Assert.Equal("NodeStarted", payload.EventType);
                Assert.Equal(NodeId, payload.NodeId);
                Assert.Equal(RunNodeId, payload.RunNodeId);
                Assert.Equal(At, payload.Timestamp);
            }),
        new("NodeProgress", RunId.ToString(), typeof(NodeProgressPayload),
            OnRun(n => n.NodeProgressAsync(RunId, NodeId, "thinking about the problem...", 7)),
            p =>
            {
                var payload = (NodeProgressPayload)p!;
                Assert.Equal(RunId, payload.RunId);
                Assert.Equal(NodeId, payload.NodeId);
                Assert.Equal("thinking about the problem...", payload.Line);
                Assert.Equal(7, payload.Seq);
            }),
        new("WorkItemStateChanged", "work-items", typeof(WorkItemStateChangedPayload),
            OnWorkItem(n => n.WorkItemStateChangedAsync(WorkItemId, RemoteWorkItemStatus.Backlog, RemoteWorkItemStatus.WorkQueue)),
            p =>
            {
                var payload = (WorkItemStateChangedPayload)p!;
                Assert.Equal(WorkItemId, payload.WorkItemId);
                Assert.Equal(WorkItemStatus.Backlog, payload.OldStatus);
                Assert.Equal(WorkItemStatus.WorkQueue, payload.NewStatus);
            }),
        new("HumanFeedbackRequired", "work-items", typeof(HumanFeedbackRequiredPayload),
            OnWorkItem(n => n.HumanFeedbackRequiredAsync(WorkItemId, "Node failed")),
            p =>
            {
                var payload = (HumanFeedbackRequiredPayload)p!;
                Assert.Equal(WorkItemId, payload.WorkItemId);
                Assert.Equal("Node failed", payload.Reason);
            }),
        new("PreviewStateChanged", "work-items", typeof(PreviewStateChangedPayload),
            OnWorkItem(n => n.PreviewStateChangedAsync(WorkItemId)),
            p => Assert.Equal(WorkItemId, ((PreviewStateChangedPayload)p!).WorkItemId)),
        new("WorkItemRunProgressed", "work-items", typeof(WorkItemRunProgressedPayload),
            OnWorkItem(n => n.RunProgressedAsync(WorkItemId)),
            p => Assert.Equal(WorkItemId, ((WorkItemRunProgressedPayload)p!).WorkItemId)),
        new("LogEntryAppended", "work-items", typeof(LogEntryPayload),
            OnLog(n => n.LogEntryAppendedAsync(new LogEntryPayload(1, DateTimeOffset.UtcNow, "Warning", "ILD.Core.LoopEngine", "Run 7f21 stalled", null))),
            p =>
            {
                var payload = (LogEntryPayload)p!;
                Assert.Equal("Warning", payload.Level);
                Assert.Equal("Run 7f21 stalled", payload.Message);
            }),
        new("ChatEditProposalsChanged", ChatSessionId.ToString(), null,
            OnChat(n => n.EditProposalsChangedAsync(ChatSessionId)),
            p => Assert.Equal(ChatSessionId, AsJson(p).GetProperty("chatSessionId").GetGuid())),
        new("ChatGlobalVariableProposalsChanged", ChatSessionId.ToString(), null,
            OnChat(n => n.GlobalVariableProposalsChangedAsync(ChatSessionId)),
            p => Assert.Equal(ChatSessionId, AsJson(p).GetProperty("chatSessionId").GetGuid())),
    }.ToDictionary(r => r.Event);

    public static IEnumerable<object[]> Events => Rows.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Events))]
    public async Task Sends_a_single_payload_to_its_group_and_no_other(string eventName)
    {
        var row = Rows[eventName];
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(row.Group)).Returns(proxy.Object);
        object?[]? sent = null;
        proxy.Setup(p => p.SendCoreAsync(row.Event, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => sent = args)
            .Returns(Task.CompletedTask);

        await row.Send(clients.Object);

        Assert.NotNull(sent);
        var payload = Assert.Single(sent!);
        if (row.PayloadType is not null) Assert.IsType(row.PayloadType, payload);
        row.Verify(payload);
        clients.Verify(c => c.Group(It.Is<string>(g => g != row.Group)), Times.Never);
        clients.Verify(c => c.All, Times.Never);
    }
}
