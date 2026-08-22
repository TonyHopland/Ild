using ILD.Core.Services.Implementations;
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
/// The AI-traversal cap driven through the REAL <see cref="AINodeExecutor"/>.
/// Two behaviours only show up at this seam, because a
/// <see cref="ScriptedExecutor"/> has neither a session nor a capacity gate: what
/// a Resume out of a capped park actually sends the agent, and what a node the
/// provider deferred costs.
/// </summary>
public class AiTraversalCapAiNodeSeamTests
{
    private sealed class CapturingAdapter : IAgentAdapter
    {
        private readonly string? _reportSessionId;
        public CapturingAdapter(string? reportSessionId) => _reportSessionId = reportSessionId;

        public List<AgentExecutionContext> Calls { get; } = new();
        public string Name => "Capturing";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Calls.Add(context);
            // Real adapters report the live session id mid-stream; the executor
            // persists it on the run, which is what makes a later Resume able to
            // continue it — and what a cap park has to clear.
            if (_reportSessionId is not null) context.OnSessionId?.Invoke(_reportSessionId);
            return Task.FromResult(NodeExecutionResult.Ok("done", context.Prompt, _reportSessionId));
        }
    }

    private sealed class FakeRegistry : IAgentAdapterRegistry
    {
        private readonly IAgentAdapter _adapter;
        public FakeRegistry(IAgentAdapter adapter) => _adapter = adapter;
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => _adapter;
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
    }

    /// <summary>A provider that is always full, so the AI node defers before it runs.</summary>
    private sealed class AlwaysFullTracker : IAiProviderConcurrencyTracker
    {
        public bool HasCapacity(Guid providerId, int parallelism) => false;
        public bool TryEnter(Guid providerId, int parallelism) => false;
        public void Exit(Guid providerId) { }
        public int ActiveCount(Guid providerId) => int.MaxValue;
    }

    private static LoopEngineHarness Harness(
        CapturingAdapter adapter, IAiProviderConcurrencyTracker? concurrency = null)
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

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(s => s.GetByRunIdAsync(It.IsAny<Guid>(), It.IsAny<int?>()))
            .ReturnsAsync(Array.Empty<EventLogEntry>());

        return new LoopEngineHarness(configure: services =>
        {
            services.AddSingleton(providerStore.Object);
            services.AddSingleton<IAgentAdapterRegistry>(new FakeRegistry(adapter));
            services.AddSingleton<IPromptRenderingService>(sp => new PromptRenderingService(
                new PromptTemplateResolver(), eventLog.Object, sp.GetRequiredService<ILoopRunStore>()));
            if (concurrency is not null) services.AddSingleton(concurrency);
        });
    }

    /// <summary>
    /// Resuming a capped park must cold-start the pending node on its own
    /// rendered prompt. The cap fires BEFORE the node runs, so the session id on
    /// the run belongs to the node that already finished; steering into it would
    /// send "Continue where you left off." — or the human's note alone — into a
    /// conversation this node never had, skipping its prompt entirely.
    /// </summary>
    [Fact]
    public async Task Resuming_a_capped_park_runs_the_pending_node_from_its_own_prompt()
    {
        var adapter = new CapturingAdapter(reportSessionId: "sess-from-the-previous-step");
        using var h = Harness(adapter);
        await h.Db.Settings.UpsertAsync(AppSettingKeys.MaxAiTraversals, "1");
        h.AddNode("a", NodeType.AI);
        h.AddEdge("a", "a", EdgeType.OnSuccess);
        h.NodesById["a"].Config = "{\"useSession\":false,\"prompt\":\"Work on {{WorkItem.Title}}\"}";
        h.Db.Context.SaveChanges();
        h.Registry.Register(new AINodeExecutor());
        h.WorkItemsMock.Setup(m => m.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync(new WorkItemView { Id = h.WorkItemId, Title = "WI title", RepositoryId = null });

        h.SeedRun("a");
        await h.RunAsync();

        // One AI step ran, the second is what the cap refused.
        Assert.Single(adapter.Calls);
        var parked = h.ReloadRun();
        Assert.Equal(HaltReason.MaxAiTraversals, parked.HaltReason);
        Assert.Null(parked.CurrentAiSessionId);

        await h.Engine.ResumeFromHaltAsync(h.RunId, "you are going in circles");
        await h.WaitUntilIdleAsync();

        var resumed = adapter.Calls[1];
        // The node's own prompt, rendered, with the human's note appended —
        // not the note on its own, and not into the earlier session.
        Assert.Equal("Work on WI title\n\nyou are going in circles", resumed.Prompt);
        Assert.Null(resumed.SessionId);
    }

    /// <summary>
    /// A node the provider capacity gate defers never reaches a model, so it must
    /// not spend budget. Otherwise a contended run reaches the cap and tells the
    /// person "the AI ran N steps without human input" when it ran none.
    /// </summary>
    [Fact]
    public async Task A_node_deferred_by_provider_capacity_costs_no_budget()
    {
        var adapter = new CapturingAdapter(reportSessionId: null);
        using var h = Harness(adapter, new AlwaysFullTracker());
        await h.Db.Settings.UpsertAsync(AppSettingKeys.MaxAiTraversals, "2");
        h.AddNode("a", NodeType.AI);
        h.AddEdge("a", "a", EdgeType.OnSuccess);
        h.NodesById["a"].Config = "{\"prompt\":\"go\"}";
        h.Db.Context.SaveChanges();
        h.Registry.Register(new AINodeExecutor());

        h.SeedRun("a");
        // Three scheduler re-drives against a provider that stays full — more
        // than the cap, so a charged deferral would have parked the run by now.
        for (var i = 0; i < 3; i++) await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Empty(adapter.Calls);
        Assert.Equal(0, run.AiTraversalCount);
        Assert.Equal(LoopRunStatus.Running, run.Status);
        Assert.False(run.IsHalted);
        Assert.Empty(h.ReloadRunNodes());
    }
}
