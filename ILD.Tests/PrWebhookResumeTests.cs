using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// One webhook, one resume.
///
/// A changes-requested review carries its prose as the payload's comment, so the
/// webhook both signalled <c>on_rejected</c> here and woke the heartbeat — which
/// then evaluated the same pull request and had <c>on_rejected</c> to signal
/// too. Two resumes racing for one parked node, from one reviewer clicking once.
///
/// The rule is not "pulse only for comment events": that would strand an
/// approving review's body, and a rejection on a node with no <c>on_rejected</c>
/// wired, both of which the heartbeat is the only path for. It is that the pulse
/// is what happens when this path resumed nothing.
/// </summary>
public class PrWebhookResumeTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public LoopRunNode RunNode { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public Mock<IPrStatusPoller> Poller { get; } = new();
        public Mock<IEventLogStore> Events { get; } = new();
        public List<EventLog> Logged { get; } = new();

        public Harness(params string[] wiredEdges)
        {
            var loopNodeId = Guid.NewGuid();
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-1",
                PrUrl = PrUrl,
                CurrentNodeId = loopNodeId,
            };
            RunNode = new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = Run.Id,
                LoopNodeId = loopNodeId,
                Status = LoopRunNodeStatus.WaitingHuman,
            };
            Runs.Setup(s => s.GetByPrUrlAsync(PrUrl)).ReturnsAsync(Run);
            Runs.Setup(s => s.GetRunNodeAsync(Run.Id, loopNodeId)).ReturnsAsync(RunNode);
            Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync(wiredEdges.Select(name => new LoopNodeEdge
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = loopNodeId,
                    TargetNodeId = Guid.NewGuid(),
                    EdgeType = EdgeType.Custom,
                    Name = name,
                }).ToArray());
            Events.Setup(s => s.AppendAsync(It.IsAny<EventLog>()))
                .Callback<EventLog>(Logged.Add)
                .ReturnsAsync(1);
        }

        public PrSyncService Build() => new(
            Runs.Object, Events.Object, new Mock<IWorkItemManager>().Object, Engine.Object, Poller.Object);
    }

    private static WebhookPayload ChangesRequested() =>
        new("pull_request.rejected", "repo-1", "7", PrUrl, "please rename the flag", "changes_requested");

    [Fact]
    public async Task A_rejection_that_resumes_the_run_here_does_not_also_wake_the_heartbeat()
    {
        var h = new Harness(PrNodeEdges.OnRejected);

        await h.Build().HandleWebhookAsync(ChangesRequested());

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(s => s.EdgeName == PrNodeEdges.OnRejected)), Times.Once);
        h.Poller.Verify(p => p.Pulse(), Times.Never);
    }

    [Fact]
    public async Task A_rejection_on_a_node_that_does_not_wire_it_wakes_the_heartbeat_instead()
    {
        // Nothing resumed the run, and the reviewer did say something. The
        // heartbeat is the only path left to that prose, under every throttle.
        var h = new Harness(PrNodeEdges.OnComment);

        await h.Build().HandleWebhookAsync(ChangesRequested());

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        h.Poller.Verify(p => p.Pulse(), Times.Once);
    }

    [Fact]
    public async Task An_approving_review_with_something_to_say_still_wakes_the_heartbeat()
    {
        // It maps to no edge of its own, and most of what a person says on a
        // pull request is said exactly here.
        var h = new Harness(PrNodeEdges.OnComment, PrNodeEdges.OnApproved);

        await h.Build().HandleWebhookAsync(
            new WebhookPayload("pull_request.review", "repo-1", "7", PrUrl, "Nice. One thought on the naming.", null));

        h.Poller.Verify(p => p.Pulse(), Times.Once);
    }

    [Fact]
    public async Task A_plain_comment_still_wakes_the_heartbeat_and_fires_nothing_itself()
    {
        var h = new Harness(PrNodeEdges.OnComment);

        await h.Build().HandleWebhookAsync(
            new WebhookPayload("pull_request.comment", "repo-1", "7", PrUrl, "one more thing", null));

        h.Poller.Verify(p => p.Pulse(), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task A_webhook_that_says_nothing_wakes_nothing()
    {
        var h = new Harness(PrNodeEdges.OnComment);

        await h.Build().HandleWebhookAsync(
            new WebhookPayload("pull_request.merged", "repo-1", "7", PrUrl, null, "merged"));

        h.Poller.Verify(p => p.Pulse(), Times.Never);
    }

    [Fact]
    public async Task What_a_reviewer_said_is_recorded_whichever_path_the_run_resumed_on()
    {
        // The feedback log is not part of the resume decision: a rejection that
        // signalled its own edge still has to leave the prose behind it.
        var h = new Harness(PrNodeEdges.OnRejected);

        await h.Build().HandleWebhookAsync(ChangesRequested());

        var logged = Assert.Single(h.Logged);
        Assert.Equal(EventType.HumanFeedbackReceived, logged.EventType);
        Assert.Equal("please rename the flag", logged.Data);
    }
}
