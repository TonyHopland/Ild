using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public sealed class RunConversationPromptVariablesTests : IDisposable
{
    private const string AllThree = "F:[{{Conversation.Full}}] A:[{{Conversation.AI}}] H:[{{Conversation.Human}}]";

    private readonly TestDb _db = new();
    private DateTime _clock = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private LoopRunNode SeedRunNode(LoopRun run, Guid versionId, NodeType type, string label)
    {
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = versionId, NodeType = type, Label = label };
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = node.Id,
            NodeLabel = label,
            Status = LoopRunNodeStatus.Succeeded,
            StartedAt = _clock,
            CompletedAt = _clock,
        };
        _db.Context.LoopNodes.Add(node);
        _db.Context.LoopRunNodes.Add(runNode);
        _db.Context.SaveChanges();
        return runNode;
    }

    private void Event(LoopRun run, EventType type, string data, LoopRunNode? runNode = null)
    {
        _clock = _clock.AddMinutes(1);
        _db.Context.EventLogs.Add(new EventLog
        {
            LoopRunId = run.Id,
            EventType = type,
            Data = data,
            NodeId = runNode?.LoopNodeId,
            RunNodeId = runNode?.Id,
            Timestamp = _clock,
        });
        _db.Context.SaveChanges();
    }

    [Fact]
    public async Task Conversation_prompt_variables_render_the_projected_ai_turns_and_replies_as_before()
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            StartedAt = _clock,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        _db.Context.LoopTemplates.Add(template);
        _db.Context.LoopTemplateVersions.Add(version);
        _db.Context.LoopRuns.Add(run);
        _db.Context.SaveChanges();
        var rnImpl = SeedRunNode(run, version.Id, NodeType.AI, "Implementer");
        var rnPrompt = SeedRunNode(run, version.Id, NodeType.Prompt, "Prompt");
        var rnHuman = SeedRunNode(run, version.Id, NodeType.Human, "Ask");
        var rnRev = SeedRunNode(run, version.Id, NodeType.AI, "Reviewer");

        Event(run, EventType.LoopRunStarted, "Run started");
        Event(run, EventType.NodeCompleted, "first AI message", rnImpl);
        Event(run, EventType.NodeCompleted, "rendered prompt", rnPrompt);
        Event(run, EventType.HumanFeedbackRequested, "please review", rnHuman);
        Event(run, EventType.HumanFeedbackReceived, "second message from a human", rnHuman);
        Event(run, EventType.RunParked, "Run Halted", rnRev);
        Event(run, EventType.NodeCompleted, "third message, also AI", rnRev);
        var renderer = new PromptRenderingService(new PromptTemplateResolver(), _db.EventLogs,
            new RunConversationService(_db.EventLogs, _db.LoopRuns), _db.LoopRuns, _db.GlobalVariables);

        var rendered = await renderer.RenderAsync(
            AllThree, run.Id, new WorkItemView { Id = run.WorkItemId, Title = "Title" }, null);

        Assert.Equal(
            "F:[[AI · Implementer] first AI message\n\n" +
            "[Human] second message from a human\n\n" +
            "[AI · Reviewer] third message, also AI] " +
            "A:[[AI · Implementer] first AI message\n\n[AI · Reviewer] third message, also AI] " +
            "H:[second message from a human]",
            rendered);
    }
}
