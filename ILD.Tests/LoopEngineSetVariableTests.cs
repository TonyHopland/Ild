using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A Set Variable node driven through the engine with the real executor and
/// the real placeholder pipeline: what it writes, which execution the writes
/// belong to, and what the nodes after it see.
/// </summary>
public class LoopEngineSetVariableTests
{
    private sealed class CapturingAdapter : IAgentAdapter
    {
        public List<AgentExecutionContext> Calls { get; } = new();
        public string Name => "Capturing";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Calls.Add(context);
            return Task.FromResult(NodeExecutionResult.Ok("done", context.Prompt, context.SessionId ?? "sess-1"));
        }
    }

    private sealed class FakeAdapterRegistry(IAgentAdapter adapter) : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => adapter;
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
        public AdapterModelSupport GetModelSupport(string providerType) => adapter.ModelSupport;
        public AgentAdapterTraits GetTraits(string? providerType) => adapter.Traits;
    }

    private sealed class InterleavingRendering(IPromptRenderingService inner, Action<string?>? whileRendering) : IPromptRenderingService
    {
        public Task<string> RenderAsync(string? template, Guid runId, WorkItemView workItem, string? previousNodeOutput)
        {
            whileRendering?.Invoke(template);
            return inner.RenderAsync(template, runId, workItem, previousNodeOutput);
        }
    }

    private readonly CapturingAdapter _adapter = new();

    /// <param name="whileRendering">Runs as each template is rendered, before the real renderer sees it.</param>
    private LoopEngineHarness Harness(Action<string?>? whileRendering = null)
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "claude-code",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);

        var h = new LoopEngineHarness(configure: services =>
        {
            services.AddSingleton(providerStore.Object);
            services.AddSingleton<IAgentAdapterRegistry>(new FakeAdapterRegistry(_adapter));
            services.AddSingleton<IPromptRenderingService>(sp => new InterleavingRendering(new PromptRenderingService(
                new PromptTemplateResolver(), sp.GetRequiredService<IEventLogStore>(),
                new RunConversationService(sp.GetRequiredService<IEventLogStore>(), sp.GetRequiredService<ILoopRunStore>()),
                sp.GetRequiredService<ILoopRunStore>(), sp.GetRequiredService<IGlobalVariableStore>()), whileRendering));
        });
        h.Registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"), new NodeOutcome.Success(EdgeType.OnSuccess, "answer\n")));
        h.Registry.Register(new SetVariableNodeExecutor());
        h.Registry.Register(new PromptNodeExecutor());
        h.Registry.Register(new AINodeExecutor());
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"), new NodeOutcome.Terminal("done")));
        return h;
    }

    private static string Assignments(params (string Name, string Value)[] assignments)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            assignments = assignments.Select(a => new { name = a.Name, value = a.Value }),
        });

    /// <summary>
    /// Start → Set Variable → <paramref name="nextType"/> → Cleanup, and with
    /// <paramref name="fallback"/> an OnFailure edge from Set Variable to a Prompt node.
    /// </summary>
    private static void Graph(LoopEngineHarness h, string assignments, NodeType nextType, string nextConfig, bool fallback = false)
    {
        h.AddNode("start", NodeType.Start);
        h.AddNode("set", NodeType.SetVariable, "Keep the answer");
        h.AddNode("next", nextType);
        h.AddNode("done", NodeType.Cleanup);
        h.NodesById["set"].Config = assignments;
        h.NodesById["next"].Config = nextConfig;
        h.Db.Context.SaveChanges();
        h.AddEdge("start", "set", EdgeType.OnSuccess);
        h.AddEdge("set", "next", EdgeType.OnSuccess);
        h.AddEdge("next", "done", EdgeType.OnSuccess);
        if (fallback)
        {
            h.AddNode("fallback", NodeType.Prompt);
            h.AddEdge("set", "fallback", EdgeType.OnFailure);
            h.AddEdge("fallback", "done", EdgeType.OnSuccess);
        }
        h.SeedRun("start");
    }

    [Fact]
    public async Task Writes_every_assignment_in_order_as_its_own_execution_and_the_next_node_reads_them()
    {
        using var h = Harness();
        Graph(h,
            Assignments(("zulu", "{{Node.Input}}"), ("alpha", "<{{Var.zulu}}>"), ("empty", "")),
            NodeType.Prompt, """{"prompt":"{{Var.zulu}}|{{Var.alpha}}|{{Node.Input}}"}""");

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Equal(0, run.AiTraversalCount);
        var setExecution = Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["set"].Id);
        Assert.Equal(LoopRunNodeStatus.Succeeded, setExecution.Status);
        Assert.Equal("answer\n", setExecution.Output);
        var prompt = Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["next"].Id);
        Assert.Equal("answer\n|<>|answer\n", prompt.Output);

        var store = h.Services.GetRequiredService<ILoopRunStore>();
        Assert.Equal(
            new[] { ("alpha", "<>"), ("empty", ""), ("zulu", "answer\n") },
            (await store.GetVariablesAsync(h.RunId)).Select(v => (v.Name, v.Value)));
        var writes = await store.GetTurnVariableChangesForWorkItemAsync(h.WorkItemId);
        Assert.Equal(new[] { "zulu", "alpha", "empty" }, writes.Select(w => w.Name));
        Assert.All(writes, w => Assert.Equal(setExecution.Id, w.RunNodeId));
    }

    [Fact]
    public async Task A_write_the_database_refuses_fails_the_node_onto_its_failure_edge()
    {
        using var h = Harness();
        Graph(h, Assignments(("handoff", "{{Node.Input}}")), NodeType.Prompt, """{"prompt":"after"}""", fallback: true);
        h.Db.Context.Database.ExecuteSqlRaw("DROP TABLE LoopRunVariables");

        await h.RunAsync();

        var set = Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["set"].Id);
        Assert.Equal(LoopRunNodeStatus.Failed, set.Status);
        Assert.Contains("handoff", set.Error);
        Assert.Contains(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["fallback"].Id);
        Assert.DoesNotContain(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["next"].Id);
    }

    [Fact]
    public async Task An_AI_node_after_it_resolves_a_session_placeholder_on_the_variable_it_set()
    {
        using var h = Harness();
        Graph(h, Assignments(("current_ticket", "T-7")),
            NodeType.AI, """{"prompt":"go","useSession":true,"sessionPlaceholder":"ticket_{{Var.current_ticket}}"}""");

        await h.RunAsync();

        var ai = Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["next"].Id);
        Assert.Equal(LoopRunNodeStatus.Succeeded, ai.Status);
        var binding = await h.Services.GetRequiredService<ILoopRunStore>()
            .GetSessionBindingAsync(h.RunId, nameof(NodeType.AI), "ticket_T-7");
        Assert.Equal("sess-1", binding?.SessionId);
        Assert.Equal(1, h.ReloadRun().AiTraversalCount);
    }

    [Fact]
    public async Task Its_writes_do_not_undo_a_cancel_that_lands_while_it_runs()
    {
        LoopEngineHarness? harness = null;
        using var h = harness = Harness(whileRendering: template =>
        {
            if (template != "cancel now") return;
            using var other = harness!.Db.Fresh();
            other.LoopRuns.Single(r => r.Id == harness.RunId).Status = LoopRunStatus.Cancelled;
            other.SaveChanges();
        });
        Graph(h, Assignments(("first", "cancel now"), ("second", "kept")), NodeType.Prompt, """{"prompt":"after"}""");

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Cancelled, h.ReloadRun().Status);
        Assert.Equal(
            new[] { ("first", "cancel now"), ("second", "kept") },
            (await h.Services.GetRequiredService<ILoopRunStore>().GetVariablesAsync(h.RunId)).Select(v => (v.Name, v.Value)));
        Assert.DoesNotContain(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["next"].Id);
    }

    private const string Doubled = """{"assignments":[{"name":"kept","value":"set"},{"name":"doubled","value":"{{Var.seed}}{{Var.seed}}"}]}""";

    [Fact]
    public async Task A_value_too_long_for_a_loop_variable_takes_the_failure_edge_and_sets_nothing()
    {
        using var h = Harness();
        Graph(h, Doubled, NodeType.Prompt, """{"prompt":"after"}""", fallback: true);
        var store = h.Services.GetRequiredService<ILoopRunStore>();
        await store.SetVariableAsync(h.RunId, "seed", new string('x', 5000));

        await h.RunAsync();

        var set = Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["set"].Id);
        Assert.Equal(LoopRunNodeStatus.Failed, set.Status);
        Assert.Contains("10000", set.Error);
        Assert.Contains(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["fallback"].Id);
        Assert.DoesNotContain(h.ReloadEvents(), e => e.EventType == EventType.VariableSet);
        Assert.Equal(new[] { "seed" }, (await store.GetVariablesAsync(h.RunId)).Select(v => v.Name));
    }

    [Fact]
    public async Task Without_a_failure_edge_a_value_too_long_parks_the_run_and_Retry_renders_it_again()
    {
        using var h = Harness();
        Graph(h, Doubled, NodeType.Prompt, """{"prompt":"after"}""");
        var store = h.Services.GetRequiredService<ILoopRunStore>();
        await store.SetVariableAsync(h.RunId, "seed", new string('x', 5000));

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
        Assert.Equal(h.NodesById["set"].Id, h.ReloadRun().CurrentNodeId);
        Assert.Equal(new[] { "seed" }, (await store.GetVariablesAsync(h.RunId)).Select(v => v.Name));

        await store.SetVariableAsync(h.RunId, "seed", "ok");
        var failed = Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["set"].Id);
        await h.Engine.RetryFromNodeAsync(h.RunId, failed.Id);
        await h.WaitUntilIdleAsync();

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        Assert.Equal(
            new[] { ("doubled", "okok"), ("kept", "set"), ("seed", "ok") },
            (await store.GetVariablesAsync(h.RunId)).Select(v => (v.Name, v.Value)));
    }

    [Fact]
    public async Task A_missing_global_parks_before_it_and_after_the_global_is_set_Resume_stores_its_value()
    {
        using var h = Harness();
        Graph(h, Assignments(("rules", "{{Global.Rules}}")), NodeType.Prompt, """{"prompt":"{{Var.rules}}"}""");

        await h.RunAsync();

        Assert.Equal(HaltReason.GlobalVariableMissing, h.ReloadRun().HaltReason);
        Assert.Equal(h.NodesById["set"].Id, h.ReloadRun().CurrentNodeId);
        Assert.DoesNotContain(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["set"].Id);

        await h.Services.GetRequiredService<IGlobalVariableStore>().AddAsync(new GlobalVariable
        {
            Id = Guid.NewGuid(),
            Name = "Rules",
            NormalizedName = GlobalVariable.Normalize("Rules"),
            Value = "be brief",
            UpdatedAt = DateTime.UtcNow,
        }, CancellationToken.None);
        await h.Engine.ResumeFromHaltAsync(h.RunId, null);
        await h.WaitUntilIdleAsync();

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        Assert.Equal("be brief", Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["next"].Id).Output);
    }

    [Fact]
    public async Task Each_write_logs_which_variable_it_set_and_whether_it_was_new_or_changed_but_never_the_value()
    {
        using var h = Harness();
        Graph(h, Assignments(("phase", "review"), ("ticket", "T-7"), ("handoff", "{{Node.Input}}")),
            NodeType.Prompt, """{"prompt":"after"}""");
        var store = h.Services.GetRequiredService<ILoopRunStore>();
        await store.SetVariableAsync(h.RunId, "phase", "review");
        await store.SetVariableAsync(h.RunId, "handoff", "old");

        await h.RunAsync();

        var set = Assert.Single(h.ReloadRunNodes(), rn => rn.LoopNodeId == h.NodesById["set"].Id);
        var logged = h.ReloadEvents().Where(e => e.EventType == EventType.VariableSet).ToList();
        Assert.Equal(new[] { "phase (unchanged)", "ticket (new)", "handoff (changed)" }, logged.Select(e => e.Data));
        Assert.All(logged, e =>
        {
            Assert.Equal(set.Id, e.RunNodeId);
            Assert.Equal(h.NodesById["set"].Id, e.NodeId);
        });
    }
}
