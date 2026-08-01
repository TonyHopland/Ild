using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The single-render invariant: each template field is rendered exactly once,
/// by its node executor, and the content that substitution pulls in is never
/// re-scanned for placeholders.
///
/// Content arriving through a placeholder is not a template — a work item
/// description, a loop variable, a previous node's output and a prior AI reply
/// are all attacker-agnostic prose that may legitimately quote the placeholder
/// grammar (a spec that says "write {{Var.handoff}} when you finish", an agent
/// explaining the syntax). A second render pass eats those tokens, and it runs
/// with a strictly weaker context than the first, so what it substitutes is
/// usually empty. These tests drive a real AI node against a fake agent CLI and
/// assert on the prompt that CLI was handed.
/// </summary>
public class PromptRenderRecursionTests
{
    private const string Title = "Fix the widget";

    /// <summary>
    /// Run one AI node end to end — executor render, adapter, agent process —
    /// and return the prompt the agent process actually received.
    /// </summary>
    private static async Task<string> RunAiNodeAsync(
        string nodePrompt,
        string? workItemDescription = null,
        (string Name, string Value)[]? variables = null,
        (string Label, string Output)[]? priorAiOutputs = null,
        string? previousNodeOutput = null)
    {
        using var cli = new PromptCapturingCli();
        var runId = Guid.NewGuid();

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "capture",
            Type = "claude-code",
            BaseUrl = string.Empty,
            Model = string.Empty,
            IsDefault = true,
            Parallelism = 1,
            Config = cli.ProviderConfigJson,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);

        var workItem = new WorkItemView
        {
            Id = "WI-1",
            Title = Title,
            Description = workItemDescription,
            WorktreePath = cli.WorkDir,
        };
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);

        var runStore = new Mock<ILoopRunStore>();
        runStore.Setup(s => s.GetVariablesAsync(It.IsAny<Guid>()))
            .ReturnsAsync((variables ?? Array.Empty<(string, string)>())
                .Select(v => new LoopRunVariable { LoopRunId = runId, Name = v.Name, Value = v.Value })
                .ToList());
        runStore.Setup(s => s.GetRunNodesWithNodeAsync(It.IsAny<Guid>()))
            .ReturnsAsync((priorAiOutputs ?? Array.Empty<(string, string)>())
                .Select((n, i) => new LoopRunNode
                {
                    Id = Guid.NewGuid(),
                    LoopRunId = runId,
                    LoopNodeId = Guid.NewGuid(),
                    NodeLabel = n.Label,
                    Output = n.Output,
                    CompletedAt = new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc),
                    CreatedAt = new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc),
                    LoopNode = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.AI, Label = n.Label },
                })
                .ToList());
        runStore.Setup(s => s.SetCurrentAiSessionIdAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(s => s.GetByRunIdAsync(It.IsAny<Guid>(), It.IsAny<int?>()))
            .ReturnsAsync(Array.Empty<EventLogEntry>());

        var adapter = new RecordingAdapter(new ClaudeCodeAdapter());
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));

        var services = new ServiceCollection();
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(workItems.Object);
        services.AddSingleton(runStore.Object);
        services.AddSingleton(registry);
        services.AddSingleton<IPromptTemplateResolver>(new PromptTemplateResolver());
        services.AddSingleton<IPromptRenderingService>(sp => new PromptRenderingService(
            sp.GetRequiredService<IPromptTemplateResolver>(), eventLog.Object, runStore.Object));
        var sp = services.BuildServiceProvider();

        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            NodeType = NodeType.AI,
            Config = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["prompt"] = nodePrompt,
            }),
        };
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = "WI-1",
            WorktreePath = cli.WorkDir,
            PreviousNodeOutput = previousNodeOutput,
        };

        var executor = new AINodeExecutor();
        await foreach (var _ in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
        {
        }

        var sent = cli.CapturedPrompt;
        // Whatever the node executor resolved is what the agent must receive:
        // no layer downstream of the single render pass may touch the prompt.
        Assert.Equal(adapter.LastContext!.Prompt, sent);
        return sent;
    }

    [Fact]
    public async Task A_placeholder_quoted_in_the_work_item_description_survives_into_the_agent_prompt()
    {
        // A spec that tells the agent which loop variable to write is ordinary
        // prose. Re-scanning it turns the instruction into an empty gap, and the
        // agent then confidently writes that emptiness back into the loop.
        var sent = await RunAiNodeAsync(
            nodePrompt: "Spec:\n{{WorkItem.Description}}",
            workItemDescription: "Write your summary into {{Var.handoff}} before you finish.");

        Assert.Equal("Spec:\nWrite your summary into {{Var.handoff}} before you finish.", sent);
    }

    [Fact]
    public async Task A_placeholder_inside_a_loop_variable_value_survives_into_the_agent_prompt()
    {
        // The variable's value was written by an earlier agent turn; it is data,
        // not a template, so the token it mentions must reach the next agent
        // intact rather than being resolved against a different node's context.
        var sent = await RunAiNodeAsync(
            nodePrompt: "Handoff: {{Var.handoff}}",
            variables: [("handoff", "the reviewer asked for {{WorkItem.Title}} in the title")]);

        Assert.Equal("Handoff: the reviewer asked for {{WorkItem.Title}} in the title", sent);
        Assert.DoesNotContain(Title, sent);
    }

    [Fact]
    public async Task A_placeholder_inside_a_prior_AI_output_survives_into_the_agent_prompt()
    {
        // An agent explaining the templating grammar to the next node is the
        // exact case that first surfaced this: its answer gets holed on replay.
        var sent = await RunAiNodeAsync(
            nodePrompt: "Earlier:\n{{Conversation.AI}}",
            priorAiOutputs: [("Explainer", "Use {{EventLog.LastN}} to see recent events.")]);

        Assert.Equal("Earlier:\n[AI · Explainer] Use {{EventLog.LastN}} to see recent events.", sent);
    }

    [Fact]
    public async Task A_placeholder_inside_the_previous_node_output_survives_into_the_agent_prompt()
    {
        var sent = await RunAiNodeAsync(
            nodePrompt: "{{Node.Input}}",
            workItemDescription: "the description that must not leak in",
            previousNodeOutput: "Human said: keep {{WorkItem.Description}} out of this prompt.");

        Assert.Equal("Human said: keep {{WorkItem.Description}} out of this prompt.", sent);
        Assert.DoesNotContain("must not leak in", sent);
    }

    [Fact]
    public async Task Substituted_content_is_scanned_no_more_than_once_however_deeply_it_nests()
    {
        // Two levels at once: the description pulled in by the node's template
        // quotes a variable, and that variable's own value quotes another token.
        // Exactly one pass runs, so only the node's own template expands.
        var sent = await RunAiNodeAsync(
            nodePrompt: "{{WorkItem.Description}} / {{Var.handoff}}",
            workItemDescription: "set {{Var.handoff}}",
            variables: [("handoff", "mention {{WorkItem.Title}}")]);

        Assert.Equal("set {{Var.handoff}} / mention {{WorkItem.Title}}", sent);
    }

    [Fact]
    public async Task An_unknown_placeholder_is_left_alone_exactly_as_the_resolver_leaves_it()
    {
        // Unknown names pass through the resolver untouched; nothing downstream
        // may take a second look and decide otherwise.
        var sent = await RunAiNodeAsync(nodePrompt: "See {{Totally.Unknown}} and <Foo.Bar> below.");

        Assert.Equal("See {{Totally.Unknown}} and <Foo.Bar> below.", sent);
    }
}
