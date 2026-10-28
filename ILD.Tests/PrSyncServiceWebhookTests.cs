using ILD.Data;
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
/// What a pull-request webhook does to the run parked on that pull request.
///
/// One webhook, one resume. A changes-requested review carries its prose as the
/// payload's comment, so the webhook both signalled <c>on_rejected</c> here and
/// woke the heartbeat — which then evaluated the same pull request and had
/// <c>on_rejected</c> to signal too. Two resumes racing for one parked node,
/// from one reviewer clicking once.
///
/// The rule is not "pulse only for comment events": that would strand an
/// approving review's body, and a rejection on a node with no <c>on_rejected</c>
/// wired, both of which the heartbeat is the only path for. It is that the pulse
/// is what happens when this path resumed nothing. A webhook may only make the
/// heartbeat early: if it fired the comment edge on its own path it would bypass
/// every throttle — the delivered ledger, the marker, the incomplete-review
/// check — because none of them live here.
/// </summary>
public class PrSyncServiceWebhookTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public LoopRunNode RunNode { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public Mock<IPrStatusPoller> Poller { get; } = new();
        public Mock<IEventLogService> Events { get; } = new();
        public Mock<IWorkItemManager> WorkItems { get; } = new();
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
            Runs.Setup(s => s.GetActiveByWorkItemAsync(Run.WorkItemId)).ReturnsAsync(Run);
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
            Events.Setup(s => s.AppendAsync(It.IsAny<Guid>(), It.IsAny<EventType>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
                .Callback<Guid, EventType, string, Guid?, Guid?, string?>((run, type, data, node, runNode, edge) => Logged.Add(
                    new EventLog { LoopRunId = run, EventType = type, Data = data, NodeId = node, RunNodeId = runNode, EdgeName = edge }))
                .ReturnsAsync(1);
        }

        public PrSyncService Build() => new(
            Runs.Object, Events.Object, WorkItems.Object, Engine.Object, Poller.Object, TestActors.Instance);

        public Task HandleAsync(string eventName, string? comment = null, string? state = null)
            => Build().HandleWebhookAsync(new WebhookPayload(eventName, "repo-1", "7", PrUrl, comment, state));

        public void VerifySignalled(string edgeName)
            => Engine.Verify(e => e.SignalNodeResultAsync(Run.Id, RunNode.Id,
                It.Is<NodeSignal>(s => s.EdgeName == edgeName)), Times.Once);

        public void VerifyNothingSignalled()
            => Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Theory]
    [InlineData("pull_request.rejected")]
    [InlineData("pull_request_review.submitted")]
    public async Task A_rejection_that_resumes_the_run_here_does_not_also_wake_the_heartbeat(string eventName)
    {
        var h = new Harness(LoopOutputs.OnRejected);

        await h.HandleAsync(eventName, "please rename the flag", "changes_requested");

        h.VerifySignalled(LoopOutputs.OnRejected);
        h.Poller.Verify(p => p.Pulse(), Times.Never);
    }

    [Fact]
    public async Task A_rejection_on_a_node_that_does_not_wire_it_wakes_the_heartbeat_instead()
    {
        // Nothing resumed the run, and the reviewer did say something. The
        // heartbeat is the only path left to that prose, under every throttle.
        var h = new Harness(LoopOutputs.OnComment);

        await h.HandleAsync("pull_request.rejected", "please rename the flag", "changes_requested");

        h.VerifyNothingSignalled();
        h.Poller.Verify(p => p.Pulse(), Times.Once);
    }

    [Fact]
    public async Task An_approving_review_with_something_to_say_still_wakes_the_heartbeat()
    {
        // It maps to no edge of its own, and most of what a person says on a
        // pull request is said exactly here.
        var h = new Harness(LoopOutputs.OnComment, LoopOutputs.OnApproved);

        await h.HandleAsync("pull_request.review", "Nice. One thought on the naming.");

        h.Poller.Verify(p => p.Pulse(), Times.Once);
    }

    [Theory]
    [InlineData("pull_request.comment")]
    [InlineData("pull_request_review_comment.created")]
    public async Task A_comment_webhook_wakes_the_heartbeat_and_fires_nothing_itself(string eventName)
    {
        var h = new Harness(LoopOutputs.OnComment);

        await h.HandleAsync(eventName, "one more thing");

        h.Poller.Verify(p => p.Pulse(), Times.Once);
        h.VerifyNothingSignalled();
    }

    [Fact]
    public async Task What_a_reviewer_said_is_recorded_whichever_path_the_run_resumed_on()
    {
        // The feedback log is not part of the resume decision: a rejection that
        // signalled its own edge still has to leave the prose behind it.
        var h = new Harness(LoopOutputs.OnRejected);

        await h.HandleAsync("pull_request.rejected", "please rename the flag", "changes_requested");

        var logged = Assert.Single(h.Logged);
        Assert.Equal(EventType.HumanFeedbackReceived, logged.EventType);
        Assert.Equal("please rename the flag", logged.Data);
        Assert.Null(logged.NodeId);
        Assert.Null(logged.RunNodeId);
        Assert.Null(logged.EdgeName);
    }

    [Fact]
    public async Task A_webhook_for_a_pull_request_no_run_owns_wakes_nothing()
    {
        var h = new Harness(LoopOutputs.OnComment);
        h.Runs.Setup(s => s.GetByPrUrlAsync(It.IsAny<string>())).ReturnsAsync((LoopRun?)null);

        await h.Build().HandleWebhookAsync(
            new WebhookPayload("issue_comment.created", "repo-1", "7", "https://github.com/team/repo/pull/999", "hello?", null));

        h.Poller.Verify(p => p.Pulse(), Times.Never);
    }

    [Fact]
    public async Task A_merge_signals_on_merged_and_marks_the_run_merged()
    {
        var h = new Harness(LoopOutputs.OnMerged);

        await h.HandleAsync("pull_request.merged", state: "merged");

        h.Runs.Verify(s => s.SetPrMergedAsync(h.Run.Id), Times.Once);
        h.VerifySignalled(LoopOutputs.OnMerged);
    }

    [Fact]
    public async Task A_merge_with_no_on_merged_edge_is_recorded_without_signalling_or_waking_anything()
    {
        // The merge is still recorded, but no signal is emitted (emitting an
        // unconnected custom edge would fail the run), and a merge carries
        // nothing for the heartbeat to read.
        var h = new Harness(LoopOutputs.OnComment);

        await h.HandleAsync("pull_request.merged", state: "merged");

        h.Runs.Verify(s => s.SetPrMergedAsync(h.Run.Id), Times.Once);
        h.VerifyNothingSignalled();
        h.Poller.Verify(p => p.Pulse(), Times.Never);
    }

    [Fact]
    public async Task A_merge_of_a_stale_runs_pr_does_not_touch_the_work_item_or_the_current_run()
    {
        // Re-running a work item opens a new PR per run; the old run's PR stays
        // open. Merging it must not mark the current run merged or finish the
        // work item out from under the active run.
        var h = new Harness();
        h.Run.Status = LoopRunStatus.Failed;
        h.Run.CurrentNodeId = null;
        var currentRun = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "wi-1", Status = LoopRunStatus.Running };
        h.Runs.Setup(s => s.GetLatestByWorkItemAsync("wi-1")).ReturnsAsync(currentRun);
        h.Runs.Setup(s => s.GetRunNodesAsync(h.Run.Id)).ReturnsAsync(Array.Empty<LoopRunNode>());

        await h.HandleAsync("pull_request.merged", state: "merged");

        h.Runs.Verify(s => s.SetPrMergedAsync(h.Run.Id), Times.Once);
        h.Runs.Verify(s => s.SetPrMergedAsync(currentRun.Id), Times.Never);
        h.WorkItems.Verify(s => s.TransitionAsync(It.IsAny<WorkItemActor>(), It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
        Assert.False(currentRun.IsPrMerged);
    }

    [Fact]
    public async Task A_merge_of_a_terminal_current_runs_pr_finishes_the_work_item()
    {
        // Human merged the PR despite the run having failed: with no engine
        // path left to resume, the merge finishes the work item directly.
        var h = new Harness();
        h.Run.Status = LoopRunStatus.Failed;
        h.Run.CurrentNodeId = null;
        h.Runs.Setup(s => s.GetLatestByWorkItemAsync("wi-1")).ReturnsAsync(h.Run);
        h.Runs.Setup(s => s.GetRunNodesAsync(h.Run.Id)).ReturnsAsync(Array.Empty<LoopRunNode>());

        await h.HandleAsync("pull_request.merged", state: "merged");

        h.Runs.Verify(s => s.SetPrMergedAsync(h.Run.Id), Times.Once);
        h.WorkItems.Verify(s => s.TransitionAsync(It.IsAny<WorkItemActor>(), "wi-1", RemoteWorkItemStatus.Done,
            It.IsAny<string?>(), It.IsAny<string?>(), h.Run.Id, It.IsAny<string?>()), Times.Once);
    }
}
