using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Core.Services.RunHosts;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// "Waiting for ILD" in the engine: a node whose agent ended its turn with processes still running is parked, not finished, with
/// its session kept and the processes left alone; ILD's wake-up and a person's Restart or Resume are the ways out.
/// </summary>
public class LoopEngineWaitingChildrenTests
{
    private static readonly RunChildProcess TestRun = new(4321, "dotnet test ILD.Tests", "/tmp/tests.output");

    private static (LoopEngineHarness Harness, FakeRunHost Host, ScriptedExecutor Agent) Harness(params NodeOutcome[][] turns)
    {
        var host = new FakeRunHost();
        host.LeaveRunning(TestRun.Pid, TestRun.CommandLine, TestRun.OutputPath);
        var h = new LoopEngineHarness(configure: services => services.AddSingleton<IRunHost>(host));
        h.AddNode("ai", NodeType.AI, "Tester");
        h.AddNode("next", NodeType.Cmd);
        h.AddNode("recover", NodeType.Cmd);
        h.AddEdge("ai", "next", EdgeType.OnSuccess);
        h.AddEdge("ai", "recover", EdgeType.OnFailure);
        var agent = new ScriptedExecutor(NodeType.AI, turns[0]);
        foreach (var turn in turns.Skip(1)) agent.Then(turn);
        h.Registry.Register(agent);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd, new NodeOutcome.NodeStarting(), new NodeOutcome.Terminal("done")));
        h.SeedRun("ai");
        return (h, host, agent);
    }

    private static NodeOutcome[] EndsWithTestsRunning(string said = "Waiting on the full run; I'll report when it finishes.")
        => [new NodeOutcome.NodeStarting("run the tests"), new NodeOutcome.WaitingChildren([TestRun], said)];

    private static NodeOutcome[] Finishes() => [new NodeOutcome.NodeStarting("continue"), new NodeOutcome.Success(EdgeType.OnSuccess, "All 400 passed.")];

    private static void KeepSession(LoopEngineHarness h, string sessionId = "sess-1")
    {
        h.Db.Context.LoopRuns.Where(r => r.Id == h.RunId).ExecuteUpdate(s => s.SetProperty(r => r.CurrentAiSessionId, sessionId));
        foreach (var entry in h.Db.Context.ChangeTracker.Entries<LoopRun>().ToList()) entry.Reload();
    }

    // -- The state transition ------------------------------------------------------------------

    [Fact]
    public async Task A_node_whose_turn_ends_with_children_running_parks_as_waiting_for_ild()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning());
        using var _h = h;

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(HaltReason.WaitingForChildren, run.HaltReason);
        Assert.Equal(HumanFeedbackReasons.WaitingForChildren, run.HumanFeedbackReason);
        Assert.Equal(h.NodesById["ai"].Id, run.CurrentNodeId);
        Assert.Equal(1, run.ChildWaitCycleCount);
        var remembered = ChildWaitState.TryParse(run.ChildWaitJson)!;
        Assert.Equal(TestRun, Assert.Single(remembered.Processes));
        Assert.True(LoopRunWaiting(run));
    }

    private static bool LoopRunWaiting(LoopRun run) => run.Status == LoopRunStatus.WaitingHuman && run.IsHalted && run.HaltReason == HaltReason.WaitingForChildren;

    [Fact]
    public async Task The_node_is_not_finished_failed_or_routed_on()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning());
        using var _h = h;

        await h.RunAsync();

        var node = Assert.Single(h.ReloadRunNodes());
        Assert.Equal(LoopRunNodeStatus.WaitingIld, node.Status);
        // What the agent said this turn stays with the node.
        Assert.Equal("Waiting on the full run; I'll report when it finishes.", node.Output);
        Assert.DoesNotContain(h.ReloadEvents(), e => e.EventType is EventType.NodeCompleted or EventType.NodeFailed or EventType.EdgeTraversed);
    }

    [Fact]
    public async Task The_park_keeps_the_session_and_worktree_and_does_not_touch_the_children()
    {
        var (h, host, _) = Harness(EndsWithTestsRunning());
        using var _h = h;
        KeepSession(h);

        await h.RunAsync();

        Assert.Equal("sess-1", h.ReloadRun().CurrentAiSessionId);
        Assert.Empty(host.KillAllCalls);
        Assert.Empty(host.StoppedProcesses);
        Assert.Contains(TestRun.Pid, host.RunningPids);
        Assert.Empty(host.Deleted);
    }

    [Fact]
    public async Task The_event_log_names_each_detected_process_by_pid_and_command_line()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning());
        using var _h = h;

        await h.RunAsync();

        var parked = Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.RunParked);
        Assert.Contains("Waiting for ILD", parked.Data);
        Assert.Contains("pid 4321: dotnet test ILD.Tests", parked.Data);
        Assert.Equal(Assert.Single(h.ReloadRunNodes()).Id, parked.RunNodeId);
    }

    [Fact]
    public async Task The_work_item_waits_for_ild_rather_than_asking_a_person_or_the_throttle_gate()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning());
        using var _h = h;

        await h.RunAsync();

        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.WaitingForIld,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
            HumanFeedbackReasons.WaitingForChildren), Times.Once);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), It.IsAny<string>(), RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task The_park_is_not_recoverable_on_startup_and_is_not_a_throttle_park()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning());
        using var _h = h;

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.False(run.IsRecoverable);
        Assert.False(run.IsShutdownHalted);
        Assert.NotEqual(HaltReason.Throttled, run.HaltReason);
        Assert.Contains(await h.Db.Fresh().LoopRuns.Where(LoopRun.WaitingForChildren).ToListAsync(TestContext.Current.CancellationToken), r => r.Id == run.Id);
    }

    [Fact]
    public async Task A_node_whose_turn_ends_with_nothing_running_behaves_as_before()
    {
        var (h, _, agent) = Harness(Finishes());
        using var _h = h;

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Null(run.HaltReason);
        Assert.Null(run.ChildWaitJson);
        Assert.Equal(0, run.ChildWaitCycleCount);
        Assert.Equal(LoopRunNodeStatus.Succeeded, h.ReloadRunNodes().First().Status);
        Assert.Equal(1, agent.Invocations);
    }

    // -- The automatic wake-up --------------------------------------------------------------------

    [Fact]
    public async Task The_wake_up_continues_the_same_session_with_the_message_and_is_not_a_throttle_retry()
    {
        var (h, _, agent) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        KeepSession(h);
        await h.RunAsync();

        await h.Engine.WakeFromChildWaitAsync(h.RunId, "ILD woke you: the tests finished.");
        await h.WaitUntilIdleAsync();

        Assert.Equal(("sess-1", "ILD woke you: the tests finished."), agent.Seen[1]);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ThrottleAutoResumeCount);
        Assert.Null(run.ChildWaitJson);
        // The first attempt did its turn; only the later one is the node's outcome.
        Assert.Equal(new[] { LoopRunNodeStatus.Succeeded, LoopRunNodeStatus.Succeeded }, h.ReloadRunNodes().Where(n => n.LoopNodeId == h.NodesById["ai"].Id).Select(n => n.Status));
        // Not a human touch, so the unattended budgets keep counting.
        Assert.Equal(2, run.AiTraversalCount);
    }

    [Fact]
    public async Task The_wake_up_leaves_no_event_in_the_conversation_as_if_a_person_had_written_it()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.RunAsync();

        await h.Engine.WakeFromChildWaitAsync(h.RunId, "ILD woke you.");
        await h.WaitUntilIdleAsync();

        Assert.DoesNotContain(h.ReloadEvents(), e => e.EventType == EventType.HumanFeedbackReceived);
    }

    // -- The cycle cap ----------------------------------------------------------------------------

    private static async Task WakeAsync(LoopEngineHarness h)
    {
        await h.Engine.WakeFromChildWaitAsync(h.RunId, "woke");
        await h.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Consecutive_cycles_are_counted_and_the_node_waits_again_up_to_the_cap()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), EndsWithTestsRunning(), EndsWithTestsRunning());
        using var _h = h;
        await h.Db.Settings.UpsertAsync(AppSettingKeys.ChildrenMaxCycles, "3", TestContext.Current.CancellationToken);
        await h.RunAsync();
        await WakeAsync(h);
        await WakeAsync(h);

        var run = h.ReloadRun();
        Assert.Equal(3, run.ChildWaitCycleCount);
        Assert.Equal(HaltReason.WaitingForChildren, run.HaltReason);
    }

    [Fact]
    public async Task At_the_cap_the_run_parks_for_a_person_as_an_ordinary_halt_that_still_remembers_the_children()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), EndsWithTestsRunning(), EndsWithTestsRunning());
        using var _h = h;
        await h.Db.Settings.UpsertAsync(AppSettingKeys.ChildrenMaxCycles, "2", TestContext.Current.CancellationToken);
        await h.RunAsync();
        await WakeAsync(h);
        await WakeAsync(h);

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(HaltReason.Human, run.HaltReason);
        Assert.Equal(HumanFeedbackReasons.RunHalted, run.HumanFeedbackReason);
        // So ILD's own wait no longer picks it up, but Restart and Resume still can.
        Assert.False(LoopRunWaiting(run));
        Assert.NotNull(ChildWaitState.TryParse(run.ChildWaitJson));
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.Is<string?>(reason => reason!.Contains("2 times in a row")), It.IsAny<string?>(), It.IsAny<Guid?>(),
            HumanFeedbackReasons.RunHalted), Times.Once);
    }

    [Fact]
    public async Task The_default_cap_is_five_cycles()
    {
        Assert.Equal(5, AppSettingKeys.DefaultChildrenMaxCycles);
        var (h, _, _) = Harness(EndsWithTestsRunning(), EndsWithTestsRunning(), EndsWithTestsRunning(), EndsWithTestsRunning(), EndsWithTestsRunning(), EndsWithTestsRunning());
        using var _h = h;
        await h.RunAsync();
        for (var i = 0; i < 4; i++) await WakeAsync(h);
        Assert.Equal(HaltReason.WaitingForChildren, h.ReloadRun().HaltReason);

        await WakeAsync(h);

        Assert.Equal(HaltReason.Human, h.ReloadRun().HaltReason);
    }

    [Fact]
    public async Task A_node_that_finishes_ends_the_streak()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.RunAsync();
        await WakeAsync(h);

        Assert.Equal(0, h.ReloadRun().ChildWaitCycleCount);
    }

    [Fact]
    public async Task A_person_touching_the_run_refills_the_cycles()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.RunAsync();
        await WakeAsync(h);
        Assert.Equal(2, h.ReloadRun().ChildWaitCycleCount);

        await h.Engine.ResumeWaitingForChildrenAsync(h.RunId, null, "alice");
        await h.WaitUntilIdleAsync();

        Assert.Equal(0, h.ReloadRun().ChildWaitCycleCount);
    }

    // -- Restart -----------------------------------------------------------------------------------

    [Fact]
    public async Task Restart_stops_the_children_and_restarts_the_node_from_scratch_adding_nothing_for_the_agent()
    {
        var (h, host, agent) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        KeepSession(h);
        await h.RunAsync();

        await h.Engine.RestartWaitingForChildrenAsync(h.RunId, null, "alice");
        await h.WaitUntilIdleAsync();

        Assert.Equal([TestRun], host.StoppedProcesses);
        Assert.DoesNotContain(TestRun.Pid, host.RunningPids);
        // No note about the stopped processes: the agent gets no steering message at all.
        Assert.Null(agent.Seen[1].SteeringNote);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Null(run.ChildWaitJson);
        Assert.Equal(LoopRunNodeStatus.Interrupted, h.ReloadRunNodes().First().Status);
    }

    [Fact]
    public async Task Restart_with_input_passes_it_to_the_agent_as_a_fresh_start_and_records_it()
    {
        var (h, _, agent) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        KeepSession(h);
        await h.RunAsync();

        await h.Engine.RestartWaitingForChildrenAsync(h.RunId, "Run only the unit tests.", "alice");
        await h.WaitUntilIdleAsync();

        // From scratch: no session to continue, and the person's words come with the prompt.
        Assert.Equal((null, "Run only the unit tests."), agent.Seen[1]);
        var feedback = Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.HumanFeedbackReceived);
        Assert.Equal("Run only the unit tests.", feedback.Data);
    }

    [Fact]
    public async Task Restart_is_logged_with_the_user_the_action_and_whether_input_was_given()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.RunAsync();

        await h.Engine.RestartWaitingForChildrenAsync(h.RunId, null, "alice");
        await h.WaitUntilIdleAsync();

        var logged = Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.ChildWaitActionTaken);
        Assert.Contains("alice", logged.Data);
        Assert.Contains("Restart", logged.Data);
        Assert.Contains("input given: no", logged.Data);
        Assert.DoesNotContain(h.ReloadEvents(), e => e.EventType == EventType.HumanFeedbackReceived);
    }

    // -- Resume ------------------------------------------------------------------------------------

    [Fact]
    public async Task Resume_keeps_the_children_running_and_adds_nothing_beyond_the_human_message()
    {
        var (h, host, agent) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        KeepSession(h);
        await h.RunAsync();

        await h.Engine.ResumeWaitingForChildrenAsync(h.RunId, "Carry on.", "alice");
        await h.WaitUntilIdleAsync();

        Assert.Empty(host.StoppedProcesses);
        Assert.Contains(TestRun.Pid, host.RunningPids);
        // The same session, and exactly the person's words.
        Assert.Equal(("sess-1", "Carry on."), agent.Seen[1]);
        Assert.Null(h.ReloadRun().ChildWaitJson);
    }

    [Fact]
    public async Task Resume_without_input_continues_the_session_with_no_message_of_ilds()
    {
        var (h, _, agent) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        KeepSession(h);
        await h.RunAsync();

        await h.Engine.ResumeWaitingForChildrenAsync(h.RunId, null, "alice");
        await h.WaitUntilIdleAsync();

        // An empty note is the existing "just continue": the AI executor sends its neutral continue and nothing about the children.
        Assert.Equal(("sess-1", string.Empty), agent.Seen[1]);
        Assert.DoesNotContain(h.ReloadEvents(), e => e.EventType == EventType.HumanFeedbackReceived);
    }

    [Fact]
    public async Task Resume_is_logged_with_the_user_the_action_and_whether_input_was_given()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.RunAsync();

        await h.Engine.ResumeWaitingForChildrenAsync(h.RunId, "Carry on.", "alice");
        await h.WaitUntilIdleAsync();

        var logged = Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.ChildWaitActionTaken);
        Assert.Contains("alice", logged.Data);
        Assert.Contains("Resume", logged.Data);
        Assert.Contains("input given: yes", logged.Data);
        Assert.Equal("Carry on.", Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.HumanFeedbackReceived).Data);
    }

    [Fact]
    public async Task Restart_and_Resume_are_refused_for_a_run_that_is_not_waiting_for_its_children()
    {
        var (h, host, _) = Harness(Finishes());
        using var _h = h;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.RestartWaitingForChildrenAsync(h.RunId, null, "alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.ResumeWaitingForChildrenAsync(h.RunId, null, "alice"));
        Assert.Empty(host.StoppedProcesses);
    }

    // -- After the cap, a person can still act ---------------------------------------------------

    [Fact]
    public async Task After_the_cap_Restart_still_stops_the_children_and_Resume_still_leaves_them()
    {
        var (h, host, _) = Harness(EndsWithTestsRunning(), EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.Db.Settings.UpsertAsync(AppSettingKeys.ChildrenMaxCycles, "1", TestContext.Current.CancellationToken);
        await h.RunAsync();
        await WakeAsync(h);
        Assert.Equal(HaltReason.Human, h.ReloadRun().HaltReason);

        await h.Engine.RestartWaitingForChildrenAsync(h.RunId, null, "alice");
        await h.WaitUntilIdleAsync();

        Assert.Equal([TestRun], host.StoppedProcesses);
        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
    }

    // -- Releasing the wait to a person ----------------------------------------------------------

    [Fact]
    public async Task Releasing_the_wait_parks_the_run_for_a_person_who_can_still_restart_or_resume()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning());
        using var _h = h;
        await h.RunAsync();

        await h.Engine.ReleaseChildWaitToHumanAsync(h.RunId, "ILD could not wake the agent");

        var run = h.ReloadRun();
        Assert.Equal(HaltReason.Human, run.HaltReason);
        Assert.NotNull(ChildWaitState.TryParse(run.ChildWaitJson));
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            "ILD could not wake the agent", It.IsAny<string?>(), It.IsAny<Guid?>(), HumanFeedbackReasons.RunHalted), Times.Once);

        await h.Engine.ResumeWaitingForChildrenAsync(h.RunId, null, "alice");
    }

    [Fact]
    public async Task A_run_released_to_a_person_no_longer_shows_its_node_as_waiting_for_ild()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning());
        using var _h = h;
        await h.RunAsync();

        await h.Engine.ReleaseChildWaitToHumanAsync(h.RunId, "ILD could not wake the agent");

        Assert.Equal(LoopRunNodeStatus.Interrupted, Assert.Single(h.ReloadRunNodes()).Status);
    }

    [Fact]
    public async Task At_the_cap_the_node_no_longer_shows_as_waiting_for_ild()
    {
        var (h, _, _) = Harness(EndsWithTestsRunning(), EndsWithTestsRunning());
        using var _h = h;
        await h.Db.Settings.UpsertAsync(AppSettingKeys.ChildrenMaxCycles, "1", TestContext.Current.CancellationToken);
        await h.RunAsync();
        await WakeAsync(h);

        Assert.Equal(HaltReason.Human, h.ReloadRun().HaltReason);
        Assert.DoesNotContain(h.ReloadRunNodes(), n => n.Status == LoopRunNodeStatus.WaitingIld);
    }

    // -- ILD's wake-up only ever ends its own wait ------------------------------------------------

    private static void Stamp(LoopEngineHarness h, HaltReason reason)
    {
        h.Db.Context.LoopRuns.Where(r => r.Id == h.RunId).ExecuteUpdate(s => s.SetProperty(r => r.HaltReason, reason));
        foreach (var entry in h.Db.Context.ChangeTracker.Entries<LoopRun>().ToList()) entry.Reload();
    }

    [Theory]
    [InlineData(HaltReason.Human)]
    [InlineData(HaltReason.Throttled)]
    [InlineData(HaltReason.MaxAiTraversals)]
    public async Task The_wake_up_leaves_a_run_that_is_parked_for_another_reason_by_now(HaltReason reason)
    {
        var (h, _, agent) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.RunAsync();
        Stamp(h, reason);

        Assert.False(await h.Engine.WakeFromChildWaitAsync(h.RunId, "woke"));
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Equal(reason, run.HaltReason);
        Assert.Single(agent.Seen);
    }

    [Fact]
    public async Task The_wake_up_leaves_a_paused_run_waiting()
    {
        var (h, _, agent) = Harness(EndsWithTestsRunning(), Finishes());
        using var _h = h;
        await h.RunAsync();
        await h.Engine.PauseRunAsync(h.RunId);

        Assert.False(await h.Engine.WakeFromChildWaitAsync(h.RunId, "woke"));
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.Equal(HaltReason.WaitingForChildren, run.HaltReason);
        Assert.NotNull(ChildWaitState.TryParse(run.ChildWaitJson));
        Assert.Single(agent.Seen);
    }

    // -- A run that ends while waiting takes its children with it --------------------------------

    [Fact]
    public async Task Cancelling_a_waiting_run_stops_the_children()
    {
        var (h, host, _) = Harness(EndsWithTestsRunning());
        using var _h = h;
        await h.RunAsync();

        await h.Engine.CancelRunAsync(h.RunId);

        Assert.Equal([TestRun], host.StoppedProcesses);
        Assert.Equal(LoopRunStatus.Cancelled, h.ReloadRun().Status);
    }
}
