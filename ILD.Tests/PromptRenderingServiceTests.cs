using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

public class PromptRenderingServiceTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static readonly WorkItemView WorkItem = new()
    {
        Id = "WI-1",
        Title = "Title",
        Description = "Body",
    };

    private static LoopRunNode AiNode(string label, string output, DateTime completedAt) => new()
    {
        Id = Guid.NewGuid(),
        LoopRunId = RunId,
        LoopNodeId = Guid.NewGuid(),
        NodeLabel = label,
        Output = output,
        CompletedAt = completedAt,
        CreatedAt = completedAt,
        LoopNode = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.AI, Label = label },
    };

    private static LoopRunNode NonAiNode(NodeType type, string output, DateTime completedAt) => new()
    {
        Id = Guid.NewGuid(),
        LoopRunId = RunId,
        LoopNodeId = Guid.NewGuid(),
        Output = output,
        CompletedAt = completedAt,
        CreatedAt = completedAt,
        LoopNode = new LoopNode { Id = Guid.NewGuid(), NodeType = type, Label = type.ToString() },
    };

    private static EventLogEntry Human(string data, DateTime timestamp)
        => new(RunId, EventType.HumanFeedbackReceived.ToString(), data, Timestamp: timestamp);

    private static EventLogEntry Event(EventType type, string data, DateTime timestamp)
        => new(RunId, type.ToString(), data, Timestamp: timestamp);

    private static PromptRenderingService Build(
        IReadOnlyList<LoopRunNode> runNodes,
        IEnumerable<EventLogEntry> events)
    {
        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(s => s.GetByRunIdAsync(RunId, null)).ReturnsAsync(events);

        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetRunNodesWithNodeAsync(RunId)).ReturnsAsync(runNodes);

        return new PromptRenderingService(new PromptTemplateResolver(), eventLog.Object, store.Object);
    }

    [Fact]
    public async Task Conversation_AI_contains_only_AI_node_outputs()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = Build(
            new[]
            {
                AiNode("Implementer", "did the work", t),
                NonAiNode(NodeType.Prompt, "rendered prompt", t.AddMinutes(1)),
                NonAiNode(NodeType.Cmd, "command output", t.AddMinutes(2)),
            },
            new[] { Human("human note", t.AddMinutes(3)) });

        var result = await svc.RenderAsync("{{Conversation.AI}}", RunId, WorkItem, null);

        Assert.Equal("[AI · Implementer] did the work", result);
        Assert.DoesNotContain("rendered prompt", result);
        Assert.DoesNotContain("command output", result);
        Assert.DoesNotContain("human note", result);
    }

    [Fact]
    public async Task Conversation_Human_is_verbatim_HumanFeedback_only()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = Build(
            new[] { AiNode("Reviewer", "Reject: do it differently", t) },
            new[]
            {
                Event(EventType.HumanFeedbackRequested, "please review", t.AddMinutes(1)),
                Human("Store the token in the header instead", t.AddMinutes(2)),
                Event(EventType.NodeCompleted, "node done", t.AddMinutes(3)),
            });

        var result = await svc.RenderAsync("{{Conversation.Human}}", RunId, WorkItem, null);

        Assert.Equal("Store the token in the header instead", result);
        Assert.DoesNotContain("please review", result);
        Assert.DoesNotContain("Reject", result);
    }

    [Fact]
    public async Task Conversation_Full_interleaves_AI_and_Human_by_timestamp()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var svc = Build(
            new[]
            {
                AiNode("Implementer", "first AI message", t.AddMinutes(1)),
                AiNode("Reviewer", "third message, also AI", t.AddMinutes(3)),
            },
            new[] { Human("second message from a human", t.AddMinutes(2)) });

        var result = await svc.RenderAsync("{{Conversation.Full}}", RunId, WorkItem, null);

        var expected =
            "[AI · Implementer] first AI message\n\n" +
            "[Human] second message from a human\n\n" +
            "[AI · Reviewer] third message, also AI";
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Empty_history_renders_all_conversation_placeholders_as_empty()
    {
        var svc = Build(Array.Empty<LoopRunNode>(), Array.Empty<EventLogEntry>());

        var result = await svc.RenderAsync(
            "F:[{{Conversation.Full}}] A:[{{Conversation.AI}}] H:[{{Conversation.Human}}]",
            RunId, WorkItem, null);

        Assert.Equal("F:[] A:[] H:[]", result);
    }
}
