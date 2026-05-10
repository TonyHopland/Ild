using FluentAssertions;
using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using ILD.WorkItemServer.Domain;
using System.Text.Json;

namespace ILD.Tests;

public class LoopEngineConversationContentTests
{
    [Fact]
    public async Task Human_node_suspend_includes_previous_node_output_in_conversation()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("ai", NodeType.AI),
            ("human", NodeType.Human));
        h.AddEdge("e1", "s", "ai");
        h.AddEdge("e2", "ai", "human");
        h.Save();

        // AI node succeeds with meaningful output
        h.Fakes[NodeType.AI].Behavior = _ => NodeExecutionResult.Ok("AI analyzed the code and found 3 issues to fix");

        await h.Engine.RunAsync(h.RunId);

        // The server-side work item conversation should contain the AI's output
        var serverWi = h.ServerHarness.ServerDb.WorkItems.First(w => w.Id == h.WorkItemId);
        serverWi.Status.Should().Be(ILD.WorkItemServer.Domain.WorkItemStatus.HumanFeedback);
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var conversation = JsonSerializer.Deserialize<List<ConversationMessage>>(serverWi.ConversationJson, jsonOpts)
            ?? new List<ConversationMessage>();
        conversation.Should().NotBeEmpty("conversation should have entries");
        var aiMessage = conversation.Last();
        aiMessage.Role.Should().Be("ai");
        aiMessage.Content.Should().Be("AI analyzed the code and found 3 issues to fix");
    }

    [Fact]
    public async Task Conversation_content_trims_leading_trailing_whitespace_from_ai_output()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("ai", NodeType.AI),
            ("human", NodeType.Human));
        h.AddEdge("e1", "s", "ai");
        h.AddEdge("e2", "ai", "human");
        h.Save();

        // AI node output has leading/trailing whitespace
        h.Fakes[NodeType.AI].Behavior = _ => NodeExecutionResult.Ok("\n\n\n  actual response content  \n\n\n");

        await h.Engine.RunAsync(h.RunId);

        var serverWi = h.ServerHarness.ServerDb.WorkItems.First(w => w.Id == h.WorkItemId);
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var conversation = JsonSerializer.Deserialize<List<ConversationMessage>>(serverWi.ConversationJson, jsonOpts)
            ?? new List<ConversationMessage>();
        var content = conversation.Last().Content;
        content.Should().Be("actual response content");
    }

    [Fact]
    public async Task Human_feedback_reason_stays_as_base_reason_for_ui_routing()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("ai", NodeType.AI),
            ("human", NodeType.Human));
        h.AddEdge("e1", "s", "ai");
        h.AddEdge("e2", "ai", "human");
        h.Save();

        h.Fakes[NodeType.AI].Behavior = _ => NodeExecutionResult.Ok("AI response with details");

        await h.Engine.RunAsync(h.RunId);

        // The LoopRun's HumanFeedbackReason must remain the base reason
        // so frontend exact-match checks (=== "Human Input Needed") still work
        var run = h.ReloadRun();
        run.HumanFeedbackReason.Should().Be("Human Input Needed");
    }
}
