using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// What a run's conversation shows a reader: each AI reply once, and run events
/// under names a person reads rather than the event types behind them.
/// </summary>
public class RunConversationProjectionTests
{
    private static async Task<IReadOnlyList<RunConversationMessage>> ConversationAsync(LoopEngineHarness h)
        => (await new RunConversationService(h.Db.EventLogs, h.Db.LoopRuns).GetPageAsync(h.RunId))!.Messages;

    [Fact]
    public async Task A_run_parked_for_feedback_shows_the_reply_it_asks_about_once()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Coder");
        h.AddNode("human", NodeType.Human, "Review");
        h.AddEdge("ai", "human", EdgeType.OnSuccess);
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("ai"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "the plan")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("human"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, "the plan")));

        h.SeedRun("ai");
        await h.RunAsync();

        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        Assert.Contains(h.ReloadEvents(), e => e.EventType == EventType.HumanFeedbackRequested);
        var messages = await ConversationAsync(h);
        var reply = Assert.Single(messages, m => m.Text == "the plan");
        Assert.Equal((RunConversationMessage.Ai, "Coder"), (reply.Role, reply.Name));
        Assert.DoesNotContain(messages, m => m.Name == nameof(EventType.HumanFeedbackRequested));
    }

    [Theory]
    [InlineData(EventType.LoopRunStarted, "Run started")]
    [InlineData(EventType.RunParked, "Run parked")]
    [InlineData(EventType.RecoveryTriggered, "Recovery")]
    [InlineData(EventType.LoopRunCompleted, "Run completed")]
    [InlineData(EventType.LoopRunFailed, "Run failed")]
    [InlineData(EventType.LoopRunCancelled, "Run cancelled")]
    public async Task A_run_event_is_a_system_message_with_a_readable_name(EventType type, string name)
    {
        using var db = new TestDb();
        var run = RunTimeline.SeedRun(db, "WI-1", RunTimeline.SeedVersion(db), LoopRunStatus.Running);
        var events = new[] { new EventLog { Id = 1, LoopRunId = run.Id, EventType = type, Data = "why", Timestamp = DateTime.UtcNow } };

        var message = Assert.Single(await new RunConversationService(db.EventLogs, db.LoopRuns).ProjectAsync(run.Id, events));

        Assert.Equal((RunConversationMessage.System, name, "why"), (message.Role, message.Name, message.Text));
    }

    [Fact]
    public async Task No_system_message_is_named_by_its_event_type()
    {
        using var db = new TestDb();
        var run = RunTimeline.SeedRun(db, "WI-1", RunTimeline.SeedVersion(db), LoopRunStatus.Running);
        var events = RunConversationEvents.System
            .Select((type, i) => new EventLog { Id = i + 1, LoopRunId = run.Id, EventType = type, Data = "why", Timestamp = DateTime.UtcNow })
            .ToList();

        var messages = await new RunConversationService(db.EventLogs, db.LoopRuns).ProjectAsync(run.Id, events);

        Assert.Equal(events.Count, messages.Count);
        Assert.All(messages, m => Assert.DoesNotContain(m.Name, Enum.GetNames<EventType>()));
    }
}
