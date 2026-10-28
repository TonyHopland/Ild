using ILD.Data;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class PrSyncServiceTests
{
    private static LoopNodeEdge CustomEdge(Guid sourceNodeId, string name) => new()
    {
        Id = Guid.NewGuid(),
        SourceNodeId = sourceNodeId,
        TargetNodeId = Guid.NewGuid(),
        EdgeType = EdgeType.Custom,
        Name = name,
    };

    [Fact]
    public async Task HandleWebhookAsync_carries_the_reason_on_the_signal_like_the_heartbeat_does()
    {
        // The webhook and heartbeat resume paths must leave the node in the same
        // state, including what {{PreviousNode.Output}} says: the review comment
        // the webhook brought, plus the failing checks the last poll recorded.
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            PrUrl = "https://github.com/team/repo/pull/7",
            CurrentNodeId = Guid.NewGuid(),
            PrSnapshot = PrSnapshotJson.Serialize(
                new RemotePrSnapshot("t", "b", "open", false, null, null, RemotePrCiStatus.Failed,
                    new[] { new RemotePrCheck("build", "failure", "https://ci/build", "tsc: 3 errors", "991") },
                    false, true, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow)),
        };
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = run.CurrentNodeId!.Value,
            Status = LoopRunNodeStatus.WaitingHuman,
        };

        var loopRuns = new Mock<ILoopRunStore>();
        loopRuns.Setup(s => s.GetByPrUrlAsync(run.PrUrl!)).ReturnsAsync(run);
        loopRuns.Setup(s => s.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value)).ReturnsAsync(runNode);
        loopRuns.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(runNode.LoopNodeId, LoopOutputs.OnRejected) });

        var engine = new Mock<ILoopEngine>();
        var service = new PrSyncService(loopRuns.Object, new Mock<IEventLogService>().Object,
            new Mock<IWorkItemManager>().Object, engine.Object, Mock.Of<IPrStatusPoller>(), TestActors.Instance);

        await service.HandleWebhookAsync(new WebhookPayload(
            "pull_request.rejected", "repo-1", "7", run.PrUrl, "rename the flag", "changes_requested"));

        engine.Verify(s => s.SignalNodeResultAsync(run.Id, runNode.Id,
            It.Is<NodeSignal>(signal => signal.Output != null
                && signal.Output.Contains("requested changes")
                && signal.Output.Contains("rename the flag"))), Times.Once);
    }

    [Fact]
    public async Task HandleWebhookAsync_still_carries_a_reason_when_no_snapshot_was_ever_polled()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            PrUrl = "https://github.com/team/repo/pull/7",
            CurrentNodeId = Guid.NewGuid(),
            PrSnapshot = "{ not json",
        };
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = run.CurrentNodeId!.Value,
            Status = LoopRunNodeStatus.WaitingHuman,
        };

        var loopRuns = new Mock<ILoopRunStore>();
        loopRuns.Setup(s => s.GetByPrUrlAsync(run.PrUrl!)).ReturnsAsync(run);
        loopRuns.Setup(s => s.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value)).ReturnsAsync(runNode);
        loopRuns.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(runNode.LoopNodeId, LoopOutputs.OnAbandoned) });

        var engine = new Mock<ILoopEngine>();
        var service = new PrSyncService(loopRuns.Object, new Mock<IEventLogService>().Object,
            new Mock<IWorkItemManager>().Object, engine.Object, Mock.Of<IPrStatusPoller>(), TestActors.Instance);

        await service.HandleWebhookAsync(new WebhookPayload(
            "pull_request.closed", "repo-1", "7", run.PrUrl, null, "closed"));

        engine.Verify(s => s.SignalNodeResultAsync(run.Id, runNode.Id,
            It.Is<NodeSignal>(signal => !string.IsNullOrWhiteSpace(signal.Output))), Times.Once);
    }

    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private static PrSyncService PrSync(LoopEngineHarness h)
        => new(h.Db.LoopRuns, h.Services.GetRequiredService<IEventLogService>(), h.WorkItemsMock.Object, h.Engine,
            Mock.Of<IPrStatusPoller>(), TestActors.Instance);

    private static void GivePr(LoopEngineHarness h)
    {
        var run = h.Db.Context.LoopRuns.Single(r => r.Id == h.RunId);
        run.PrUrl = PrUrl;
        h.Db.Context.SaveChanges();
    }

    private static Task MergeAsync(LoopEngineHarness h)
        => PrSync(h).HandleWebhookAsync(new WebhookPayload("pull_request.merged", "repo-1", "7", PrUrl, null, "merged"));

    private static void VerifyItemFinished(LoopEngineHarness h, Func<Times> times)
        => h.WorkItemsMock.Verify(s => s.TransitionAsync(It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.Done,
            It.IsAny<string?>(), It.IsAny<string?>(), h.RunId, It.IsAny<string?>()), times);

    private static void ParkAs(LoopEngineHarness h, string humanFeedbackReason, bool halted = false)
    {
        var run = h.Db.Context.LoopRuns.Single(r => r.Id == h.RunId);
        run.HumanFeedbackReason = humanFeedbackReason;
        run.IsHalted = halted;
        h.Db.Context.SaveChanges();
    }

    [Fact]
    public async Task A_merge_of_a_run_parked_on_a_failed_node_ends_the_run_and_finishes_the_item()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI, "Agent");
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("work"),
            new NodeOutcome.Fail(EdgeType.OnFailure, "exit=1 stderr=")));
        h.SeedRun("a");
        await h.RunAsync();
        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        GivePr(h);

        await MergeAsync(h);

        Assert.Equal(LoopRunStatus.Cancelled, h.ReloadRun().Status);
        Assert.Null(await h.Db.LoopRuns.GetActiveByWorkItemAsync(h.WorkItemId));
        VerifyItemFinished(h, Times.Once);
    }

    [Fact]
    public async Task A_merge_of_a_run_parked_on_a_crash_ends_the_run_and_finishes_the_item()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI, "Agent");
        h.SeedRun("a", LoopRunStatus.WaitingHuman);
        ParkAs(h, HumanFeedbackReasons.RunCrashed);
        GivePr(h);

        await MergeAsync(h);

        Assert.Equal(LoopRunStatus.Cancelled, h.ReloadRun().Status);
        VerifyItemFinished(h, Times.Once);
    }

    [Fact]
    public async Task A_merge_does_not_finish_a_parked_run_that_is_no_longer_its_items_latest()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI, "Agent");
        var parked = h.SeedRun("a", LoopRunStatus.WaitingHuman);
        ParkAs(h, HumanFeedbackReasons.NodeFailed);
        GivePr(h);
        RunTimeline.SeedRun(h.Db, h.WorkItemId, h.TemplateVersionId, LoopRunStatus.Completed,
            startedAt: parked.StartedAt!.Value.AddMinutes(5));

        await MergeAsync(h);

        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        VerifyItemFinished(h, Times.Never);
    }

    public enum WaitingRun { AtPrNode, HaltedByAPerson, HaltedOnTheNodeThatFailed }

    [Theory]
    [InlineData(WaitingRun.AtPrNode)]
    [InlineData(WaitingRun.HaltedByAPerson)]
    [InlineData(WaitingRun.HaltedOnTheNodeThatFailed)]
    public async Task A_merge_does_not_finish_a_run_waiting_on_a_person_at_a_node(WaitingRun shape)
    {
        using var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.PR, "Pull request");
        h.SeedRun("pr", LoopRunStatus.WaitingHuman);
        switch (shape)
        {
            case WaitingRun.AtPrNode: ParkAs(h, HumanFeedbackReasons.PrAwaitingMerge); break;
            case WaitingRun.HaltedByAPerson: ParkAs(h, HumanFeedbackReasons.RunHalted, halted: true); break;
            case WaitingRun.HaltedOnTheNodeThatFailed: ParkAs(h, HumanFeedbackReasons.NodeFailed, halted: true); break;
        }
        GivePr(h);

        await MergeAsync(h);

        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        Assert.NotNull(await h.Db.LoopRuns.GetActiveByWorkItemAsync(h.WorkItemId));
        VerifyItemFinished(h, Times.Never);
    }
}
