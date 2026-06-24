using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Regression tests for the feedback-resume path: when a parked feedback node is
/// resumed via <see cref="ILoopEngine.SignalNodeResultAsync"/>, the run returns to
/// <see cref="LoopRunStatus.Running"/> and the work item must be moved back out of
/// the HumanFeedback column. Both the automated PR/CI poller and an actual human
/// response funnel through the same handler, so both are covered here.
/// </summary>
public class EngineFeedbackResumeTransitionTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private static RemotePrSnapshot CiFailedSnapshot()
        => new("t", "b", "open", false, null, null, RemotePrCiStatus.Failed, false, false,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    [Fact]
    public async Task PrStatusPoll_firing_on_ci_failed_moves_work_item_back_to_Running()
    {
        // Automated path, zero human involvement: a PR node parks in
        // PrAwaitingMerge (work item -> HumanFeedback), the PR poller sees CI red
        // and fires the wired on_ci_failed edge through SignalNodeResultAsync. The
        // run must resume to Running AND the work item must be transitioned back.
        using var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.PR);
        h.AddNode("coder", NodeType.Cmd);
        h.AddEdge("pr", "coder", EdgeType.Custom, PrNodeEdges.OnCiFailed);

        var prExec = new ScriptedExecutor(NodeType.PR,
            new NodeOutcome.NodeStarting("open pr"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.PrAwaitingMerge, "prompt"));
        prExec.Then(
            new NodeOutcome.NodeStarting("re-entry"),
            new NodeOutcome.Success(EdgeType.Custom, "ci-failed", PrNodeEdges.OnCiFailed));
        h.Registry.Register(prExec);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("coder"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("pr");
        await h.RunAsync();

        // The PR executor parks awaiting merge; the real node also records the PR
        // URL on the run, which the poller keys off to discover the parked run.
        var parked = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, parked.Status);
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, parked.HumanFeedbackReason);
        var tracked = h.Db.Context.LoopRuns.First(r => r.Id == h.RunId);
        tracked.PrUrl = PrUrl;
        h.Db.Context.SaveChanges();

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestSnapshotAsync("https://github.com/team/repo", "7"))
            .ReturnsAsync(CiFailedSnapshot());
        var poller = new PrStatusPollService(
            h.Db.LoopRuns, remote.Object, h.Engine, new Mock<IRunNotifier>().Object,
            NullLogger<PrStatusPollService>.Instance);

        await poller.PollOnceAsync();

        // Let the resumed run drive to completion so no transition is in flight
        // while we assert.
        await WaitUntilAsync(() => h.ReloadRun().Status == LoopRunStatus.Completed, TimeSpan.FromSeconds(5));

        // The single Running transition is the one the resume must perform; the
        // core loop itself never transitions a work item to Running.
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.Running,
            It.IsAny<string?>(), It.IsAny<string?>(), h.RunId, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Human_response_moves_work_item_back_to_Running()
    {
        // Human path: park at a Human node (work item -> HumanFeedback), a human
        // signals a response, the run resumes and the work item must follow.
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);
        h.AddNode("after", NodeType.Cmd);
        h.AddEdge("h", "after", EdgeType.Custom, "Respond");

        var humanExec = new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt"));
        humanExec.Then(
            new NodeOutcome.NodeStarting("re-entry"),
            new NodeOutcome.Success(EdgeType.Custom, "human-said-yes", "Respond"));
        h.Registry.Register(humanExec);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("after"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("h");
        await h.RunAsync();

        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        var waitingNode = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);

        await h.Engine.SignalNodeResultAsync(h.RunId, waitingNode.Id,
            NodeSignal.Custom("Respond", "user-text"));

        await WaitUntilAsync(() => h.ReloadRun().Status == LoopRunStatus.Completed, TimeSpan.FromSeconds(5));

        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.Running,
            It.IsAny<string?>(), It.IsAny<string?>(), h.RunId, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Rejected_signal_does_not_transition_work_item()
    {
        // Edge case: a signal targeting a node that is not WaitingHuman is rejected
        // before any state change, so no spurious Running transition must fire.
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);

        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt")));

        h.SeedRun("h");
        await h.RunAsync();

        await h.Engine.SignalNodeResultAsync(h.RunId, Guid.NewGuid(),
            new NodeSignal(ExternalActionResultType.Success, Output: "user-text"));

        await Task.Delay(200);
        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<string>(), RemoteWorkItemStatus.Running,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (predicate()) return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Shared in-memory SQLite connection may be mid-flight.
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Predicate did not become true within timeout");
    }
}
