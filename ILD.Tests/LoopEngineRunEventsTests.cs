using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Every turn of a run's story the engine tells is an event on that run, with
/// the whole text: parks, halts, steering, stops, failures, crashes and the end.
/// </summary>
public class LoopEngineRunEventsTests
{
    private static IReadOnlyList<EventLog> Events(LoopEngineHarness h) => RunTimeline.Events(h.Db, h.RunId);
    private static IReadOnlyList<EventLog> Events(LoopEngineHarness h, EventType type) => RunTimeline.Events(h.Db, h.RunId, type);

    [Fact]
    public async Task A_run_reaching_its_end_records_that_it_completed_once()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("cmd", NodeType.Cmd);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd, new NodeOutcome.NodeStarting("go"), new NodeOutcome.Terminal("done")));
        h.SeedRun("cmd");

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        Assert.Equal(EventType.LoopRunCompleted, Assert.Single(RunTimeline.EndingEvents(h.Db, h.RunId)).EventType);
    }

    [Theory]
    [InlineData("Read the plan in PLAN.md and say whether it holds up.", "Read the plan in PLAN.md and say whether it holds up.")]
    [InlineData("", HumanFeedbackReasons.HumanInputNeeded)]
    public async Task Parking_for_a_human_records_what_they_are_asked_on_the_parked_execution(string prompt, string expected)
    {
        using var h = new LoopEngineHarness();
        h.AddNode("human", NodeType.Human, "Plan check");
        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, prompt)));
        h.SeedRun("human");

        await h.RunAsync();

        var parked = Assert.Single(h.ReloadRunNodes());
        var requested = Assert.Single(Events(h, EventType.HumanFeedbackRequested));
        Assert.Equal(expected, requested.Data);
        Assert.Equal(parked.Id, requested.RunNodeId);
    }

    [Fact]
    public async Task Halting_records_the_interrupted_execution_and_the_park_on_it()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Coder");
        h.SeedRun("ai");
        var running = RunTimeline.SeedRunNode(h.Db, h.RunId, h.NodesById["ai"], LoopRunNodeStatus.Running);

        await h.Engine.HaltRunAsync(h.RunId);

        Assert.Equal(running.Id, Assert.Single(Events(h, EventType.NodeInterrupted)).RunNodeId);
        Assert.Equal(running.Id, Assert.Single(Events(h, EventType.RunParked)).RunNodeId);
        Assert.Empty(RunTimeline.EndingEvents(h.Db, h.RunId));
    }

    [Theory]
    [InlineData("Keep the public API as it is; only change the internals.", true)]
    [InlineData("   ", false)]
    public async Task Resuming_a_halt_with_a_steering_note_records_the_note_as_the_humans_words(string note, bool recorded)
    {
        using var h = new LoopEngineHarness();
        h.Registry.Register(new ScriptedExecutor(NodeType.AI));
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai");
        await h.Engine.HaltRunAsync(h.RunId);

        await h.Engine.ResumeFromHaltAsync(h.RunId, note);
        await h.WaitUntilIdleAsync();

        var replies = Events(h, EventType.HumanFeedbackReceived);
        if (recorded)
            Assert.Equal(note, Assert.Single(replies).Data);
        else
            Assert.Empty(replies);
    }

    [Theory]
    [InlineData(LoopRunStatus.Completed)]
    [InlineData(LoopRunStatus.Failed)]
    [InlineData(LoopRunStatus.Cancelled)]
    public async Task Retrying_a_node_of_a_run_that_ended_without_an_ending_event_is_refused(LoopRunStatus ended)
    {
        using var h = new LoopEngineHarness();
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd, new NodeOutcome.NodeStarting("again"), new NodeOutcome.Terminal("done")));
        h.AddNode("cmd", NodeType.Cmd);
        h.SeedRun("cmd", ended);
        var node = RunTimeline.SeedRunNode(h.Db, h.RunId, h.NodesById["cmd"], LoopRunNodeStatus.Failed);

        var refused = await Record.ExceptionAsync(() => h.Engine.RetryFromNodeAsync(h.RunId, node.Id));
        await h.WaitUntilIdleAsync();

        Assert.IsAssignableFrom<InvalidOperationException>(refused);
        Assert.Equal(ended, h.ReloadRun().Status);
        Assert.Empty(Events(h));
    }

    [Fact]
    public async Task An_automatic_resume_is_not_recorded_as_a_persons_reply()
    {
        using var h = new LoopEngineHarness();
        h.Registry.Register(new ScriptedExecutor(NodeType.AI));
        h.AddNode("ai", NodeType.AI);
        h.SeedRun("ai", LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Throttled);

        await h.Engine.ResumeFromHaltAsync(h.RunId, ThrottledRunResumeSweeper.AutomaticResumeNote, automatic: true);
        await h.WaitUntilIdleAsync();

        Assert.Empty(Events(h, EventType.HumanFeedbackReceived));
    }

    [Fact]
    public async Task A_provider_interruption_is_recorded_as_an_interruption_and_the_park_keeps_the_full_reason()
    {
        const string reason = "Provider throttled this AI node — Resume will continue the same agent session where it left off.";
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Coder");
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("work"),
            new NodeOutcome.Interrupted(reason, "You've hit your session limit · resets 9:40am (UTC)")));
        h.SeedRun("ai");

        await h.RunAsync();

        var execution = Assert.Single(h.ReloadRunNodes());
        Assert.Equal(execution.Id, Assert.Single(Events(h, EventType.NodeInterrupted)).RunNodeId);
        Assert.Empty(Events(h, EventType.Error));
        Assert.Contains(reason, Assert.Single(Events(h, EventType.RunParked)).Data);
    }

    [Fact]
    public async Task Parking_at_the_AI_step_cap_records_the_full_reason()
    {
        using var h = new LoopEngineHarness();
        await h.Db.Settings.UpsertAsync(AppSettingKeys.MaxAiTraversals, "1");
        h.AddNode("ai", NodeType.AI, "Coder");
        h.Registry.Register(new ScriptedExecutor(NodeType.AI, new NodeOutcome.NodeStarting("x"), new NodeOutcome.Terminal("x")));
        var run = h.SeedRun("ai");
        run.AiTraversalCount = 1;
        h.Db.Context.SaveChanges();

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        Assert.Contains("The AI ran 1 steps without human input (limit 1)", Assert.Single(Events(h, EventType.RunParked)).Data);
    }

    [Fact]
    public async Task Stopping_a_run_records_the_cancellation_with_its_reason_once()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("human", NodeType.Human);
        h.SeedRun("human", LoopRunStatus.WaitingHuman);

        await h.Engine.StopRunAsync(h.RunId, "Superseded by the plan in WI-12; nothing here is needed any more");
        await h.Engine.StopRunAsync(h.RunId, "a second stop");
        await h.Engine.CancelRunAsync(h.RunId);

        var cancelled = Assert.Single(RunTimeline.EndingEvents(h.Db, h.RunId));
        Assert.Equal(EventType.LoopRunCancelled, cancelled.EventType);
        Assert.Contains("Superseded by the plan in WI-12; nothing here is needed any more", cancelled.Data);
    }

    [Fact]
    public async Task A_node_failing_with_nowhere_to_go_records_the_run_failure_with_the_full_reason_on_that_execution()
    {
        var reason = "dotnet test exited with code 1:\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"  failed: Suite.Test{i}"));
        using var h = new LoopEngineHarness();
        h.AddNode("cmd", NodeType.Cmd, "Tests");
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("dotnet test"),
            new NodeOutcome.Fail(EdgeType.OnFailure, reason)));
        h.SeedRun("cmd");

        await h.RunAsync();

        var failedNode = Assert.Single(h.ReloadRunNodes());
        var failed = Assert.Single(RunTimeline.EndingEvents(h.Db, h.RunId));
        Assert.Equal(EventType.LoopRunFailed, failed.EventType);
        Assert.Contains(reason, failed.Data);
        Assert.Equal(failedNode.Id, failed.RunNodeId);
    }

    [Fact]
    public async Task A_named_output_with_no_edge_records_the_run_failure_naming_the_output()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Reviewer");
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("review"),
            new NodeOutcome.Success(EdgeType.Custom, "looks fine", "approve")));
        h.SeedRun("ai");

        await h.RunAsync();

        var failed = Assert.Single(RunTimeline.EndingEvents(h.Db, h.RunId));
        Assert.Equal(EventType.LoopRunFailed, failed.EventType);
        Assert.Contains("missing edge connection: approve", failed.Data);
    }

    private sealed class ThrowingExecutor(NodeType type, Exception exception) : INodeExecutor
    {
        public NodeType NodeType => type;
        public IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx) => throw exception;
    }

    [Fact]
    public async Task A_crash_records_the_run_failure_with_the_whole_exception_chain_however_long()
    {
        var outer = "Persisting the node result failed: " + string.Concat(Enumerable.Repeat("context of the failure; ", 30));
        const string inner = "23502: null value in column \"AiProvider\" violates not-null constraint";
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);
        h.Registry.Register(new ThrowingExecutor(NodeType.AI, new InvalidOperationException(outer, new Exception(inner))));
        h.SeedRun("ai");

        await h.LaunchAsync();
        await h.WaitUntilIdleAsync();

        Assert.Equal(LoopRunStatus.Failed, h.ReloadRun().Status);
        var failed = Assert.Single(RunTimeline.EndingEvents(h.Db, h.RunId));
        Assert.Equal(EventType.LoopRunFailed, failed.EventType);
        Assert.True(failed.Data!.Length > LoopEngine.MaxHumanFeedbackReasonLength, "the event keeps what the 512-character column cannot");
        Assert.Contains(outer.TrimEnd(), failed.Data);
        Assert.Contains(inner, failed.Data);
    }

    [Fact]
    public async Task Retrying_a_node_of_an_ended_run_is_refused_and_changes_nothing()
    {
        using var h = new LoopEngineHarness();
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd, new NodeOutcome.NodeStarting("again"), new NodeOutcome.Terminal("done")));
        h.AddNode("cmd", NodeType.Cmd);
        h.SeedRun("cmd", LoopRunStatus.Failed);
        var failedNode = RunTimeline.SeedRunNode(h.Db, h.RunId, h.NodesById["cmd"], LoopRunNodeStatus.Failed);
        await h.Services.GetRequiredService<IEventLogService>()
            .AppendAsync(h.RunId, EventType.LoopRunFailed, "tests failed", h.NodesById["cmd"].Id, failedNode.Id);
        var before = h.ReloadRun();
        var eventsBefore = Events(h).Count;

        var refused = await Record.ExceptionAsync(() => h.Engine.RetryFromNodeAsync(h.RunId, failedNode.Id));
        await h.WaitUntilIdleAsync();

        Assert.IsAssignableFrom<InvalidOperationException>(refused);
        Assert.False(string.IsNullOrWhiteSpace(refused!.Message));
        var after = h.ReloadRun();
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.CurrentNodeId, after.CurrentNodeId);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
        var node = Assert.Single(h.ReloadRunNodes());
        Assert.Equal(LoopRunNodeStatus.Failed, node.Status);
        Assert.Equal(eventsBefore, Events(h).Count);
        Assert.DoesNotContain(h.WorkItemsMock.Invocations, i => i.Method.Name == nameof(IWorkItemManager.TransitionAsync));
    }

    [Fact]
    public async Task A_parking_run_takes_answers_only_once_its_item_waits_on_a_person_and_its_question_is_recorded()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("human", NodeType.Human, "Plan check");
        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, "Does the plan hold up?")));
        h.SeedRun("human");
        LoopRunStatus? runWhenItemParked = null;
        h.WorkItemsMock.Setup(m => m.TransitionAsync(h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .Callback(() => runWhenItemParked = h.ReloadRun().Status)
            .ReturnsAsync(true);

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Running, runWhenItemParked);
        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        Assert.Single(Events(h, EventType.HumanFeedbackRequested));
    }

    [Fact]
    public async Task A_run_stopped_while_it_parks_stays_stopped()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("human", NodeType.Human, "Plan check");
        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, "Does the plan hold up?")));
        h.SeedRun("human");
        h.WorkItemsMock.Setup(m => m.TransitionAsync(h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .Returns(async () =>
            {
                await h.Engine.StopRunAsync(h.RunId, HumanFeedbackReasons.RunCancelled);
                return true;
            });

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Cancelled, h.ReloadRun().Status);
        Assert.Empty(Events(h, EventType.HumanFeedbackRequested));
    }

    [Fact]
    public async Task An_event_the_engine_could_not_record_is_logged_and_the_run_goes_on()
    {
        var logger = new RecordingLogger();
        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<EventType>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        using var h = new LoopEngineHarness(configure: s => s.AddSingleton(eventLog.Object), logger: logger);
        h.AddNode("cmd", NodeType.Cmd);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd, new NodeOutcome.NodeStarting("go"), new NodeOutcome.Terminal("done")));
        h.SeedRun("cmd");

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        var warning = Assert.Single(logger.Warnings, w => w.Text.Contains(nameof(EventType.LoopRunCompleted)));
        Assert.Contains(h.RunId.ToString(), warning.Text);
        Assert.Equal("database unavailable", warning.Exception?.Message);
    }

    private sealed class RecordingLogger : ILogger<LoopEngine>
    {
        public List<(string Text, Exception? Exception)> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add((formatter(state, exception), exception));
        }
    }
}
