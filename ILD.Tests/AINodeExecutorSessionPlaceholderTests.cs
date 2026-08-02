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
/// Covers the AI node's templated session fields: a <c>{{Var.&lt;name&gt;}}</c>
/// in <c>sessionPlaceholder</c>/<c>forkFromPlaceholder</c> gives one session per
/// distinct variable value, resolves once for both the lookup and the bind, and
/// fails the node loudly rather than resolving to something that would silently
/// collapse every iteration onto one shared session.
/// </summary>
public class AINodeExecutorSessionPlaceholderTests
{
    /// <summary>Captures the context handed to the adapter and echoes a stable session id back.</summary>
    private sealed class CapturingAdapter : IAgentAdapter
    {
        public AgentExecutionContext? Captured { get; private set; }
        public string Name => "stub";
        public string[] SupportedProviderTypes => ["stub"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Captured = context;
            return Task.FromResult(NodeExecutionResult.Ok("done", sessionId: context.SessionId ?? "new-sess"));
        }
    }

    private static (IServiceProvider sp, CapturingAdapter adapter, Mock<ILoopRunStore> runStore) BuildServices(
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, string>? bindings = null)
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "stub",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);

        var adapter = new CapturingAdapter();
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));

        var runStore = new Mock<ILoopRunStore>();
        runStore.Setup(s => s.GetVariablesAsync(It.IsAny<Guid>()))
            .ReturnsAsync(() => (variables ?? new Dictionary<string, string>())
                .Select(kv => new LoopRunVariable { Name = kv.Key, Value = kv.Value })
                .ToList());
        runStore.Setup(s => s.GetSessionBindingAsync(It.IsAny<Guid>(), "AI", It.IsAny<string>()))
            .ReturnsAsync((Guid _, string _, string placeholder) =>
                bindings is not null && bindings.TryGetValue(placeholder, out var sessionId)
                    ? new LoopRunSessionBinding { AdapterName = "AI", PlaceholderId = placeholder, SessionId = sessionId }
                    : null);

        var wi = new WorkItemView { Id = "WI-1", RepositoryId = null };
        var workItems = Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>()) == Task.FromResult<WorkItemView?>(wi));

        var services = new ServiceCollection();
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(runStore.Object);
        services.AddSingleton(workItems);
        services.AddSingleton(registry);
        return (services.BuildServiceProvider(), adapter, runStore);
    }

    private static LoopNode MakeNode(string configJson) => new()
    {
        Id = Guid.NewGuid(),
        NodeType = NodeType.AI,
        Config = configJson,
    };

    private static LoopRun MakeRun() => new() { Id = Guid.NewGuid(), WorkItemId = "WI-1" };

    private static string SessionConfig(string placeholder, string? forkFrom = null)
        => forkFrom is null
            ? $$"""{"useSession":true,"sessionPlaceholder":{{System.Text.Json.JsonSerializer.Serialize(placeholder)}}}"""
            : $$"""{"useSession":true,"sessionPlaceholder":{{System.Text.Json.JsonSerializer.Serialize(placeholder)}},"forkFromPlaceholder":{{System.Text.Json.JsonSerializer.Serialize(forkFrom)}}}""";

    private static async Task<List<NodeOutcome>> RunAsync(IServiceProvider sp, LoopRun run, LoopNode node)
    {
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new AINodeExecutor().ExecuteAsync(
            new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);
        return outcomes;
    }

    [Fact]
    public async Task Templated_placeholder_binds_under_the_resolved_name()
    {
        var (sp, _, _) = BuildServices(variables: new Dictionary<string, string> { ["ticket"] = "42" });

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("ticket_{{Var.ticket}}")));

        var bound = Assert.IsType<NodeOutcome.SessionBound>(outcomes.Single(o => o is NodeOutcome.SessionBound));
        Assert.Equal("ticket_42", bound.SessionPlaceholder);
    }

    [Fact]
    public async Task Same_variable_value_resumes_the_session_it_named_before()
    {
        var (sp, adapter, _) = BuildServices(
            variables: new Dictionary<string, string> { ["ticket"] = "42" },
            bindings: new Dictionary<string, string> { ["ticket_42"] = "sess-for-42" });

        await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("ticket_{{Var.ticket}}")));

        Assert.Equal("sess-for-42", adapter.Captured!.IncomingSessionId);
    }

    [Fact]
    public async Task A_different_variable_value_starts_a_distinct_session()
    {
        // The same node config, same bound sessions — only the variable differs,
        // so the run must not resume ticket 42's conversation on ticket 43.
        var (sp, adapter, _) = BuildServices(
            variables: new Dictionary<string, string> { ["ticket"] = "43" },
            bindings: new Dictionary<string, string> { ["ticket_42"] = "sess-for-42" });

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("ticket_{{Var.ticket}}")));

        Assert.Null(adapter.Captured!.IncomingSessionId);
        var bound = Assert.IsType<NodeOutcome.SessionBound>(outcomes.Single(o => o is NodeOutcome.SessionBound));
        Assert.Equal("ticket_43", bound.SessionPlaceholder);
    }

    [Fact]
    public async Task Lookup_and_bind_agree_on_one_resolved_name()
    {
        var (sp, adapter, runStore) = BuildServices(
            variables: new Dictionary<string, string> { ["ticket"] = "42" },
            bindings: new Dictionary<string, string> { ["ticket_42"] = "sess-for-42" });

        var run = MakeRun();
        var outcomes = await RunAsync(sp, run, MakeNode(SessionConfig("ticket_{{Var.ticket}}")));

        // One resolution feeds both sites: the binding is read under the same
        // name it is written back under, so a resumed session is also the
        // recorded one.
        runStore.Verify(s => s.GetSessionBindingAsync(run.Id, "AI", "ticket_42"), Times.Once);
        var bound = Assert.IsType<NodeOutcome.SessionBound>(outcomes.Single(o => o is NodeOutcome.SessionBound));
        Assert.Equal("ticket_42", bound.SessionPlaceholder);
        Assert.Equal("sess-for-42", adapter.Captured!.IncomingSessionId);
    }

    [Fact]
    public async Task Templated_fork_source_resolves_against_the_same_variables()
    {
        var (sp, adapter, _) = BuildServices(
            variables: new Dictionary<string, string> { ["ticket"] = "42" },
            bindings: new Dictionary<string, string> { ["base_42"] = "base-sess" });

        await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("fork_{{Var.ticket}}", "base_{{Var.ticket}}")));

        Assert.Equal("base-sess", adapter.Captured!.ForkFromSessionId);
    }

    [Fact]
    public async Task Templated_fork_source_with_no_bound_session_still_starts_fresh()
    {
        // Resolution succeeded; the source simply has nothing bound yet. That
        // stays a silent fall-through to a fresh session, as with a literal.
        var (sp, adapter, _) = BuildServices(variables: new Dictionary<string, string> { ["ticket"] = "42" });

        await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("fork_{{Var.ticket}}", "base_{{Var.ticket}}")));

        Assert.Null(adapter.Captured!.ForkFromSessionId);
        Assert.Null(adapter.Captured.SessionId);
    }

    [Fact]
    public async Task Unset_variable_fails_the_node_rather_than_sharing_one_session()
    {
        var (sp, adapter, _) = BuildServices(variables: new Dictionary<string, string> { ["other"] = "x" });

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("ticket_{{Var.ticket}}")));

        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes));
        Assert.Contains("ticket", fail.Reason);
        Assert.Contains("not set", fail.Reason);
        // Loud means the agent never ran, not that it ran under a wrong name.
        Assert.Null(adapter.Captured);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.NodeStarting);
    }

    [Fact]
    public async Task Placeholder_resolving_to_nothing_fails_the_node()
    {
        var (sp, _, _) = BuildServices(variables: new Dictionary<string, string> { ["ticket"] = "  " });

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("{{Var.ticket}}")));

        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes));
        Assert.Contains("empty session name", fail.Reason);
    }

    [Fact]
    public async Task Placeholder_resolving_past_the_binding_key_limit_fails_the_node()
    {
        var (sp, _, _) = BuildServices(variables: new Dictionary<string, string> { ["ticket"] = new string('x', 200) });

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("{{Var.ticket}}")));

        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes));
        Assert.Contains("128", fail.Reason);
    }

    [Fact]
    public async Task A_non_variable_placeholder_saved_before_this_check_fails_at_run_time()
    {
        var (sp, _, _) = BuildServices();

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("s_{{PreviousNode.Output}}")));

        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes));
        Assert.Contains("Var.", fail.Reason);
    }

    [Fact]
    public async Task A_literal_placeholder_never_reads_the_variable_store()
    {
        var (sp, adapter, runStore) = BuildServices(
            bindings: new Dictionary<string, string> { ["research"] = "sess-research" });

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode(SessionConfig("research")));

        Assert.Equal("sess-research", adapter.Captured!.IncomingSessionId);
        var bound = Assert.IsType<NodeOutcome.SessionBound>(outcomes.Single(o => o is NodeOutcome.SessionBound));
        Assert.Equal("research", bound.SessionPlaceholder);
        runStore.Verify(s => s.GetVariablesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task A_node_without_a_session_never_reads_the_variable_store()
    {
        var (sp, _, runStore) = BuildServices();

        var outcomes = await RunAsync(sp, MakeRun(), MakeNode("""{"prompt":"hi"}"""));

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.SessionBound);
        runStore.Verify(s => s.GetVariablesAsync(It.IsAny<Guid>()), Times.Never);
    }
}
