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
/// A webhook may only make the heartbeat early. If it fired the comment edge on
/// its own path it would bypass every throttle — the delivered ledger, the
/// marker, the incomplete-review check — because none of them live here.
/// </summary>
public class PrSyncServiceCommentWebhookTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public LoopRunNode RunNode { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public Mock<IPrStatusPoller> Poller { get; } = new();

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
        }

        public PrSyncService Build() => new(
            Runs.Object, new Mock<IEventLogStore>().Object, new Mock<IWorkItemManager>().Object,
            Engine.Object, Poller.Object);
    }

    [Fact]
    public async Task A_comment_webhook_wakes_the_heartbeat_instead_of_firing_the_edge()
    {
        var h = new Harness(PrNodeEdges.OnComment);

        await h.Build().HandleWebhookAsync(
            new WebhookPayload("pull_request_review_comment.created", "repo-1", "7", PrUrl, "one more thing", null));

        h.Poller.Verify(p => p.Pulse(), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task A_review_webhook_that_requests_changes_still_fires_the_edge_it_always_did()
    {
        var h = new Harness(PrNodeEdges.OnRejected);

        await h.Build().HandleWebhookAsync(
            new WebhookPayload("pull_request_review.submitted", "repo-1", "7", PrUrl, "please rename the flag", "changes_requested"));

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(s => s.EdgeName == PrNodeEdges.OnRejected)), Times.Once);
    }

    [Fact]
    public async Task A_webhook_for_a_pull_request_no_run_owns_wakes_nothing()
    {
        var h = new Harness(PrNodeEdges.OnComment);
        h.Runs.Setup(s => s.GetByPrUrlAsync(It.IsAny<string>())).ReturnsAsync((LoopRun?)null);

        await h.Build().HandleWebhookAsync(
            new WebhookPayload("issue_comment.created", "repo-1", "7", "https://github.com/team/repo/pull/999", "hello?", null));

        h.Poller.Verify(p => p.Pulse(), Times.Never);
    }
}
