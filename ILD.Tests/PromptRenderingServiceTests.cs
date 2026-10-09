using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class PromptRenderingServiceTests
{
    private static readonly WorkItemView WorkItem = new()
    {
        Id = "WI-1",
        Title = "Title",
        Description = "Body",
    };

    /// <summary>A run whose event log a test writes, and the renderer that reads it.</summary>
    private sealed class Run : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly EventLogService _events;
        private readonly Guid _versionId;
        public Guid Id { get; }
        public PromptRenderingService Renderer { get; }

        public Run()
        {
            _events = new EventLogService(_db.EventLogs);
            _versionId = RunTimeline.SeedVersion(_db);
            Id = RunTimeline.SeedRun(_db, "WI-1", _versionId, LoopRunStatus.Running).Id;
            Renderer = new PromptRenderingService(new PromptTemplateResolver(), _events,
                new RunConversationService(_db.EventLogs, _db.LoopRuns), _db.LoopRuns);
        }

        /// <summary>One execution of a fresh node of <paramref name="type"/> completing with <paramref name="output"/>.</summary>
        public Task Completed(NodeType type, string label, string output)
        {
            var node = RunTimeline.SeedNode(_db, _versionId, type, label);
            var runNode = RunTimeline.SeedRunNode(_db, Id, node, LoopRunNodeStatus.Succeeded);
            return _events.AppendAsync(Id, EventType.NodeCompleted, output, node.Id, runNode.Id);
        }

        public Task Event(EventType type, string data) => _events.AppendAsync(Id, type, data);

        public Task<string> RenderAsync(string template) => Renderer.RenderAsync(template, Id, WorkItem, null);

        public void Dispose() => _db.Dispose();
    }

    [Fact]
    public async Task Conversation_AI_contains_only_AI_node_outputs()
    {
        using var run = new Run();
        await run.Completed(NodeType.AI, "Implementer", "did the work");
        await run.Completed(NodeType.Prompt, "Prompt", "rendered prompt");
        await run.Completed(NodeType.Cmd, "Cmd", "command output");
        await run.Event(EventType.HumanFeedbackReceived, "human note");

        var result = await run.RenderAsync("{{Conversation.AI}}");

        Assert.Equal("[AI · Implementer] did the work", result);
        Assert.DoesNotContain("rendered prompt", result);
        Assert.DoesNotContain("command output", result);
        Assert.DoesNotContain("human note", result);
    }

    [Fact]
    public async Task Conversation_Human_is_verbatim_HumanFeedback_only()
    {
        using var run = new Run();
        await run.Completed(NodeType.AI, "Reviewer", "Reject: do it differently");
        await run.Event(EventType.HumanFeedbackRequested, "please review");
        await run.Event(EventType.HumanFeedbackReceived, "Store the token in the header instead");
        await run.Completed(NodeType.Cmd, "Cmd", "node done");

        var result = await run.RenderAsync("{{Conversation.Human}}");

        Assert.Equal("Store the token in the header instead", result);
        Assert.DoesNotContain("please review", result);
        Assert.DoesNotContain("Reject", result);
    }

    [Fact]
    public async Task Conversation_Full_interleaves_AI_and_Human_in_the_order_they_were_written()
    {
        using var run = new Run();
        await run.Completed(NodeType.AI, "Implementer", "first AI message");
        await run.Event(EventType.HumanFeedbackReceived, "second message from a human");
        await run.Event(EventType.RunParked, "Run Halted");
        await run.Completed(NodeType.AI, "Reviewer", "third message, also AI");

        var result = await run.RenderAsync("{{Conversation.Full}}");

        var expected =
            "[AI · Implementer] first AI message\n\n" +
            "[Human] second message from a human\n\n" +
            "[AI · Reviewer] third message, also AI";
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Empty_history_renders_all_conversation_placeholders_as_empty()
    {
        using var run = new Run();

        var result = await run.RenderAsync(
            "F:[{{Conversation.Full}}] A:[{{Conversation.AI}}] H:[{{Conversation.Human}}]");

        Assert.Equal("F:[] A:[] H:[]", result);
    }
}
