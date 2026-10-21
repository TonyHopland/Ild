using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A node that fails with nowhere to go parks its run on that node for a
/// person, rather than ending it, so they can fix the cause and retry the
/// node. Runs that ended that way before this park existed reopen on Retry.
/// </summary>
public class LoopEngineFailedNodeParkTests
{
    private const string AdapterExit = "exit=1 stderr=";

    private static IReadOnlyList<EventLog> Events(LoopEngineHarness h, EventType type) => RunTimeline.Events(h.Db, h.RunId, type);

    private static IEventLogService EventLog(LoopEngineHarness h) => h.Services.GetRequiredService<IEventLogService>();

    private static void VerifyItemMovedTo(LoopEngineHarness h, RemoteWorkItemStatus status, string? humanFeedbackReason = null)
        => h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, status,
            It.IsAny<string?>(), It.IsAny<string?>(), h.RunId,
            It.Is<string?>(r => humanFeedbackReason == null || r == humanFeedbackReason)), Times.AtLeastOnce);

    private static void AssertParkedOnFailedNode(LoopEngineHarness h, string node, LoopRunNode failed, string reason)
    {
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.False(run.IsHalted);
        Assert.Null(run.HaltReason);
        Assert.Equal(HumanFeedbackReasons.NodeFailed, run.HumanFeedbackReason);
        Assert.Equal(h.NodesById[node].Id, run.CurrentNodeId);

        Assert.Equal(LoopRunNodeStatus.Failed, failed.Status);
        Assert.Equal(reason, failed.Error);

        var parked = Assert.Single(Events(h, EventType.RunParked));
        Assert.Contains(reason, parked.Data);
        Assert.Equal(h.NodesById[node].Id, parked.NodeId);
        Assert.Equal(failed.Id, parked.RunNodeId);
        Assert.Empty(RunTimeline.EndingEvents(h.Db, h.RunId));

        VerifyItemMovedTo(h, RemoteWorkItemStatus.HumanFeedback, HumanFeedbackReasons.NodeFailed);
    }

    [Theory]
    [InlineData(NodeType.AI)]
    [InlineData(NodeType.Cmd)]
    public async Task A_node_failing_with_no_OnFailure_edge_parks_the_run_on_it_and_leaves_the_conversation_open(NodeType type)
    {
        var notifier = new RunStateRecorder();
        using var h = new LoopEngineHarness(notifier);
        h.AddNode("a", type, "Agent");
        h.Registry.Register(new ScriptedExecutor(type,
            new NodeOutcome.NodeStarting("work"),
            new NodeOutcome.Fail(EdgeType.OnFailure, AdapterExit)));
        h.SeedRun("a");

        await h.RunAsync();

        AssertParkedOnFailedNode(h, "a", Assert.Single(h.ReloadRunNodes()), AdapterExit);
        Assert.Contains((h.RunId, LoopRunStatus.Running, LoopRunStatus.WaitingHuman), notifier.Changes);

        Assert.False(await EventLog(h).HasRunEndedAsync(h.RunId));
        await EventLog(h).AppendAsync(h.RunId, EventType.HumanFeedbackReceived, "The disk was full; I freed space.");
        Assert.Single(Events(h, EventType.HumanFeedbackReceived));
    }

    [Fact]
    public async Task Retrying_the_failed_node_of_a_parked_run_runs_it_again_and_follows_its_success_edge()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI, "Agent");
        h.AddNode("b", NodeType.Cmd, "Tests");
        h.AddEdge("a", "b", EdgeType.OnSuccess);
        var agent = new ScriptedExecutor(NodeType.AI,
                new NodeOutcome.NodeStarting("work"),
                new NodeOutcome.Fail(EdgeType.OnFailure, AdapterExit))
            .Then(
                new NodeOutcome.NodeStarting("work"),
                new NodeOutcome.Success(EdgeType.OnSuccess, "implemented it"));
        h.Registry.Register(agent);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("dotnet test"),
            new NodeOutcome.Terminal("passed")));
        h.SeedRun("a");
        await h.RunAsync();
        var failed = Assert.Single(h.ReloadRunNodes());

        await h.Engine.RetryFromNodeAsync(h.RunId, failed.Id);
        await h.WaitUntilIdleAsync();

        VerifyItemMovedTo(h, RemoteWorkItemStatus.Running);
        Assert.Equal(2, agent.Invocations);
        var nodes = h.ReloadRunNodes();
        var agentRuns = nodes.Where(n => n.LoopNodeId == h.NodesById["a"].Id).ToList();
        Assert.Equal(new[] { LoopRunNodeStatus.Failed, LoopRunNodeStatus.Succeeded }, agentRuns.Select(n => n.Status));
        Assert.Equal(LoopRunNodeStatus.Succeeded, Assert.Single(nodes, n => n.LoopNodeId == h.NodesById["b"].Id).Status);
        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
    }

    [Fact]
    public async Task A_node_recovery_re_drives_after_a_restart_parks_the_run_when_it_fails_and_recovery_leaves_the_park_alone()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI, "Agent");
        var agent = new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("work"),
            new NodeOutcome.Fail(EdgeType.OnFailure, AdapterExit));
        h.Registry.Register(agent);
        h.SeedRun("a");
        var stale = RunTimeline.SeedRunNode(h.Db, h.RunId, h.NodesById["a"], LoopRunNodeStatus.Running);
        var recovery = new RecoveryManager(h.WorkItemsMock.Object, h.Db.LoopRuns, h.Db.Providers, h.Db.LoopTemplates,
            new Mock<IRepositoryManager>().Object, h.Engine, EventLog(h), TestActors.Instance);

        Assert.Contains(h.RunId, await recovery.GetRecoverableRunIdsAsync());
        Assert.True(await recovery.RecoverRunAsync(h.RunId));
        await h.WaitUntilIdleAsync();

        var failed = Assert.Single(h.ReloadRunNodes(), n => n.Id != stale.Id);
        AssertParkedOnFailedNode(h, "a", failed, AdapterExit);

        Assert.DoesNotContain(h.RunId, await recovery.GetRecoverableRunIdsAsync());
        Assert.False(await recovery.RecoverRunAsync(h.RunId));
        await h.WaitUntilIdleAsync();
        Assert.Equal(1, agent.Invocations);
        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        Assert.Null(h.ReloadRun().CompletedAt);
    }

    [Fact]
    public async Task A_crash_mid_node_parks_the_run_on_that_execution_and_retrying_it_runs_the_node_again()
    {
        using var h = new LoopEngineHarness(new CrashOnFirstNodeStartNotifier("adapter blew up"));
        h.AddNode("a", NodeType.AI, "Agent");
        h.AddNode("b", NodeType.Cmd, "Tests");
        h.AddEdge("a", "b", EdgeType.OnSuccess);
        var agent = new ScriptedExecutor(NodeType.AI,
                new NodeOutcome.NodeStarting("work"),
                new NodeOutcome.Success(EdgeType.OnSuccess, "implemented it"))
            .Then(
                new NodeOutcome.NodeStarting("work"),
                new NodeOutcome.Success(EdgeType.OnSuccess, "implemented it"));
        h.Registry.Register(agent);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("dotnet test"),
            new NodeOutcome.Terminal("passed")));
        h.SeedRun("a");

        await h.LaunchAsync();
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.False(run.IsHalted);
        var crashed = Assert.Single(h.ReloadRunNodes());
        Assert.Equal(LoopRunNodeStatus.Failed, crashed.Status);
        Assert.Contains("adapter blew up", crashed.Error);
        Assert.Empty(RunTimeline.EndingEvents(h.Db, h.RunId));
        VerifyItemMovedTo(h, RemoteWorkItemStatus.HumanFeedback, HumanFeedbackReasons.RunCrashed);

        await h.Engine.RetryFromNodeAsync(h.RunId, crashed.Id);
        await h.WaitUntilIdleAsync();

        Assert.Equal(2, agent.Invocations);
        Assert.Contains(h.ReloadRunNodes(), n => n.LoopNodeId == h.NodesById["b"].Id && n.Status == LoopRunNodeStatus.Succeeded);
        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
    }

    /// <summary>
    /// The shape a node failure with nowhere to go left a run in before it
    /// parked: ended Failed with "Node Failed", its item waiting on a person.
    /// </summary>
    private static async Task<LoopRunNode> SeedRunThatEndedOnAFailedNodeAsync(
        LoopEngineHarness h, string node, LoopRunStatus status = LoopRunStatus.Failed,
        string humanFeedbackReason = HumanFeedbackReasons.NodeFailed)
    {
        var run = h.SeedRun(node, status);
        run.CompletedAt = DateTime.UtcNow;
        run.HumanFeedbackReason = humanFeedbackReason;
        h.Db.Context.SaveChanges();
        var failed = RunTimeline.SeedRunNode(h.Db, h.RunId, h.NodesById[node], LoopRunNodeStatus.Failed);
        await EventLog(h).AppendAsync(h.RunId, EventType.LoopRunFailed, AdapterExit, h.NodesById[node].Id, failed.Id);
        return failed;
    }

    private static void ItemIs(LoopEngineHarness h, RemoteWorkItemStatus status)
        => h.WorkItemsMock.Setup(m => m.GetWorkItemAsync(h.WorkItemId))
            .ReturnsAsync(new WorkItemView { Id = h.WorkItemId, Status = status });

    [Fact]
    public async Task Retrying_the_failed_node_of_a_run_that_ended_on_it_reopens_the_run_and_its_conversation()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI, "Agent");
        h.AddNode("review", NodeType.Human, "Review");
        h.AddEdge("a", "review", EdgeType.OnSuccess);
        var agent = new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("work"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "implemented it"));
        h.Registry.Register(agent);
        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, "Does it look right?")));
        var failed = await SeedRunThatEndedOnAFailedNodeAsync(h, "a");
        ItemIs(h, RemoteWorkItemStatus.HumanFeedback);

        await h.Engine.RetryFromNodeAsync(h.RunId, failed.Id);
        await h.WaitUntilIdleAsync();

        Assert.Equal(1, agent.Invocations);
        VerifyItemMovedTo(h, RemoteWorkItemStatus.Running);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.Contains(h.ReloadRunNodes(), n => n.LoopNodeId == h.NodesById["review"].Id && n.Status == LoopRunNodeStatus.WaitingHuman);
        Assert.Single(Events(h, EventType.RunReopened));

        Assert.False(await EventLog(h).HasRunEndedAsync(h.RunId));
        await EventLog(h).AppendAsync(h.RunId, EventType.HumanFeedbackReceived, "Looks right.");
        var names = (await new RunConversationService(h.Db.EventLogs, h.Db.LoopRuns).GetPageAsync(h.RunId))!
            .Messages.Where(m => m.Role == RunConversationMessage.System).Select(m => m.Name).ToList();
        var failedAt = names.IndexOf("Run failed");
        Assert.True(failedAt >= 0, string.Join(", ", names));
        Assert.True(names.IndexOf("Run reopened") > failedAt, string.Join(", ", names));
    }

    public enum EndedRun
    {
        Completed,
        Cancelled,
        FailedForAnotherReason,
        ItemNoLongerWaitingOnAPerson,
        NotTheItemsLatestRun,
        ItemHasAnotherLiveRun,
    }

    [Theory]
    [InlineData(EndedRun.Completed)]
    [InlineData(EndedRun.Cancelled)]
    [InlineData(EndedRun.FailedForAnotherReason)]
    [InlineData(EndedRun.ItemNoLongerWaitingOnAPerson)]
    [InlineData(EndedRun.NotTheItemsLatestRun)]
    [InlineData(EndedRun.ItemHasAnotherLiveRun)]
    public async Task Retrying_a_node_of_any_other_ended_run_is_refused_and_changes_nothing(EndedRun shape)
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI, "Agent");
        var agent = new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("work"),
            new NodeOutcome.Terminal("done"));
        h.Registry.Register(agent);
        var failed = await SeedRunThatEndedOnAFailedNodeAsync(h, "a",
            shape switch
            {
                EndedRun.Completed => LoopRunStatus.Completed,
                EndedRun.Cancelled => LoopRunStatus.Cancelled,
                _ => LoopRunStatus.Failed,
            },
            shape == EndedRun.FailedForAnotherReason ? HumanFeedbackReasons.RunCrashed : HumanFeedbackReasons.NodeFailed);
        ItemIs(h, shape == EndedRun.ItemNoLongerWaitingOnAPerson ? RemoteWorkItemStatus.Backlog : RemoteWorkItemStatus.HumanFeedback);
        var stuck = h.ReloadRun();
        if (shape == EndedRun.NotTheItemsLatestRun)
            RunTimeline.SeedRun(h.Db, h.WorkItemId, h.TemplateVersionId, LoopRunStatus.Completed,
                startedAt: stuck.StartedAt!.Value.AddMinutes(5));
        if (shape == EndedRun.ItemHasAnotherLiveRun)
            RunTimeline.SeedRun(h.Db, h.WorkItemId, h.TemplateVersionId, LoopRunStatus.WaitingHuman,
                startedAt: stuck.StartedAt!.Value.AddMinutes(-5));
        var eventsBefore = RunTimeline.Events(h.Db, h.RunId).Count;

        var refused = await Record.ExceptionAsync(() => h.Engine.RetryFromNodeAsync(h.RunId, failed.Id));
        await h.WaitUntilIdleAsync();

        Assert.IsAssignableFrom<InvalidOperationException>(refused);
        var after = h.ReloadRun();
        Assert.Equal(stuck.Status, after.Status);
        Assert.Equal(stuck.CompletedAt, after.CompletedAt);
        Assert.Equal(eventsBefore, RunTimeline.Events(h.Db, h.RunId).Count);
        Assert.True(await EventLog(h).HasRunEndedAsync(h.RunId));
        Assert.Equal(0, agent.Invocations);
        Assert.DoesNotContain(h.WorkItemsMock.Invocations, i => i.Method.Name == nameof(IWorkItemManager.TransitionAsync));
    }

    /// <summary>
    /// Throws from the engine's first report that a node is running, after the
    /// execution row exists: an exception in the run loop itself, which reaches
    /// the crash handler. An executor that throws does not — the engine turns
    /// that into the node failing.
    /// </summary>
    private sealed class CrashOnFirstNodeStartNotifier(string crash) : IRunNotifier
    {
        private int _starts;

        public Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus)
            => newStatus == LoopRunNodeStatus.Running && Interlocked.Increment(ref _starts) == 1
                ? throw new InvalidOperationException(crash)
                : Task.CompletedTask;

        public Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus) => Task.CompletedTask;
        public Task EventLoggedAsync(Guid runId, long eventId, string eventType, Guid? nodeId, Guid? runNodeId, DateTime timestamp) => Task.CompletedTask;
        public Task PausedAsync(Guid runId) => Task.CompletedTask;
        public Task ResumedAsync(Guid runId) => Task.CompletedTask;
        public Task HaltedAsync(Guid runId) => Task.CompletedTask;
        public Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq) => Task.CompletedTask;
        public Task PrSnapshotChangedAsync(Guid runId) => Task.CompletedTask;
        public Task PrQueueChangedAsync(Guid runId) => Task.CompletedTask;
    }

    private sealed class RunStateRecorder : IRunNotifier
    {
        private readonly ConcurrentQueue<(Guid, LoopRunStatus, LoopRunStatus)> _changes = new();
        public IReadOnlyList<(Guid RunId, LoopRunStatus Old, LoopRunStatus New)> Changes => _changes.ToArray();

        public Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
        {
            _changes.Enqueue((runId, oldStatus, newStatus));
            return Task.CompletedTask;
        }

        public Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus) => Task.CompletedTask;
        public Task EventLoggedAsync(Guid runId, long eventId, string eventType, Guid? nodeId, Guid? runNodeId, DateTime timestamp) => Task.CompletedTask;
        public Task PausedAsync(Guid runId) => Task.CompletedTask;
        public Task ResumedAsync(Guid runId) => Task.CompletedTask;
        public Task HaltedAsync(Guid runId) => Task.CompletedTask;
        public Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq) => Task.CompletedTask;
        public Task PrSnapshotChangedAsync(Guid runId) => Task.CompletedTask;
        public Task PrQueueChangedAsync(Guid runId) => Task.CompletedTask;
    }
}
