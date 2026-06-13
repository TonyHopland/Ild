using ILD.Api.Hubs;
using ILD.Core.Services.Implementations;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ILD.Tests;

public class LoopRunHubTests
{
    private static LoopRunHub BuildHub(RunProgressBuffer buffer, out Mock<IGroupManager> groups)
    {
        groups = new Mock<IGroupManager>();
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");
        return new LoopRunHub(buffer)
        {
            Groups = groups.Object,
            Context = context.Object,
        };
    }

    [Fact]
    public async Task SubscribeToRun_joins_group_and_replays_the_captured_backlog()
    {
        var buffer = new RunProgressBuffer();
        var runId = Guid.NewGuid();
        buffer.Append(runId, "first line\n");
        buffer.Append(runId, "second line\n");

        var hub = BuildHub(buffer, out var groups);

        var snapshot = await hub.SubscribeToRun(runId);

        groups.Verify(g => g.AddToGroupAsync("conn-1", runId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("first line\nsecond line\n", snapshot.Text);
        Assert.Equal(2, snapshot.LastSeq);
    }

    [Fact]
    public async Task SubscribeToRun_returns_empty_replay_when_nothing_captured_yet()
    {
        var buffer = new RunProgressBuffer();
        var hub = BuildHub(buffer, out _);

        var snapshot = await hub.SubscribeToRun(Guid.NewGuid());

        Assert.Equal(string.Empty, snapshot.Text);
        Assert.Equal(0, snapshot.LastSeq);
    }
}
