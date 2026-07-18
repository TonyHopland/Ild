using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class AINodeExecutorTests
{
    private static NodeExecutionContext BuildCtx(
        LoopNode node,
        LoopRun run,
        IServiceProvider sp)
        => new(run, node, sp, CancellationToken.None);

    private static LoopRun MakeRun() => new()
    {
        Id = Guid.NewGuid(),
        WorkItemId = "WI-1",
    };

    private static LoopNode MakeNode(string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        NodeType = NodeType.AI,
        Config = configJson,
    };

    private static IServiceProvider BuildServices(
        IProviderStore providerStore,
        ILoopRunStore? loopRunStore = null,
        IWorkItemManager? workItemManager = null,
        IAgentAdapterRegistry? registry = null,
        WorkItemView? workItem = null,
        IAiProviderConcurrencyTracker? concurrency = null)
    {
        var wi = workItem ?? new WorkItemView { Id = "WI-1", RepositoryId = null };
        var wimMock = workItemManager ?? Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>()) == Task.FromResult<WorkItemView?>(wi));

        var lrsMock = loopRunStore ?? Mock.Of<ILoopRunStore>(m =>
            m.GetByIdAsync(It.IsAny<Guid>()) == Task.FromResult<LoopRun?>(null) &&
            m.GetRunNodesAsync(It.IsAny<Guid>()) == Task.FromResult<IReadOnlyList<LoopRunNode>>(Array.Empty<LoopRunNode>()));

        var services = new ServiceCollection();
        services.AddSingleton(providerStore);
        services.AddSingleton(lrsMock);
        services.AddSingleton(wimMock);
        // When no registry is supplied the executor fails with "No agent adapter
        // registry", which proves provider resolution succeeded. Match-rule
        // routing tests pass a fake registry so a real output is produced.
        if (registry is not null)
            services.AddSingleton(registry);
        if (concurrency is not null)
            services.AddSingleton(concurrency);
        return services.BuildServiceProvider();
    }

    /// <summary>Adapter that returns a fixed result, ignoring the request.</summary>
    private sealed class StubAdapter(NodeExecutionResult result) : IAgentAdapter
    {
        public string Name => "stub";
        public string[] SupportedProviderTypes => ["stub"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context) => Task.FromResult(result);
    }

    private static IAgentAdapterRegistry RegistryReturning(NodeExecutionResult result)
    {
        var adapter = new StubAdapter(result);
        return Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));
    }

    private static (IServiceProvider sp, Mock<IProviderStore> store) BuildServicesWithDefaultProvider(
        NodeExecutionResult adapterResult)
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
        var sp = BuildServices(providerStore.Object, registry: RegistryReturning(adapterResult));
        return (sp, providerStore);
    }

    private static async Task<NodeOutcome> LastOutcomeAsync(string configJson, NodeExecutionResult adapterResult)
    {
        var (sp, _) = BuildServicesWithDefaultProvider(adapterResult);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(configJson), MakeRun(), sp);

        NodeOutcome? last = null;
        await foreach (var o in executor.ExecuteAsync(ctx))
            last = o;
        return last!;
    }

    // Two rules both match "REJECT and review" (the first case-insensitively);
    // last-match-wins must route to whichever pattern sits later in the output.
    private const string TwoRuleConfig =
        @"{""matchRules"":[{""pattern"":""reject"",""edgeName"":""Reject""},{""pattern"":""review"",""edgeName"":""Review""}]}";

    [Fact]
    public async Task Matching_output_routes_to_last_matching_rules_custom_edge_case_insensitively()
    {
        var outcome = await LastOutcomeAsync(TwoRuleConfig, NodeExecutionResult.Ok("REJECT and review"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.Custom, success.Edge);
        // "review" sits later in the output, so it wins despite being the later rule.
        Assert.Equal("Review", success.EdgeName);
    }

    [Fact]
    public async Task Rule_order_does_not_decide_the_winner_only_position_in_the_output()
    {
        // Same two rules, reversed configuration order — the output is unchanged,
        // so the winner must be unchanged too.
        const string reversed =
            @"{""matchRules"":[{""pattern"":""review"",""edgeName"":""Review""},{""pattern"":""reject"",""edgeName"":""Reject""}]}";

        var outcome = await LastOutcomeAsync(reversed, NodeExecutionResult.Ok("REJECT and review"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal("Review", success.EdgeName);
    }

    // The regression this rule change exists for: a reviewer that narrates its
    // reasoning mentions "reject" on the way to approving, and used to be routed
    // down the reject edge.
    [Fact]
    public async Task Narrated_verdict_routes_on_the_closing_word_not_an_earlier_mention()
    {
        const string config =
            @"{""matchRules"":[{""pattern"":""reject"",""edgeName"":""Reject""},{""pattern"":""approve"",""edgeName"":""Approve""}]}";

        var outcome = await LastOutcomeAsync(
            config,
            NodeExecutionResult.Ok("I found no reason to reject, so: approve"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.Custom, success.Edge);
        Assert.Equal("Approve", success.EdgeName);
    }

    [Fact]
    public async Task A_rule_matching_repeatedly_is_judged_by_its_last_occurrence()
    {
        // "reject" appears first, but its LAST occurrence is after "approve" —
        // only that last occurrence may be compared, so Reject wins.
        var outcome = await LastOutcomeAsync(
            @"{""matchRules"":[{""pattern"":""reject"",""edgeName"":""Reject""},{""pattern"":""approve"",""edgeName"":""Approve""}]}",
            NodeExecutionResult.Ok("reject? cannot approve this — reject"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal("Reject", success.EdgeName);
    }

    [Fact]
    public async Task Two_rules_matching_at_the_same_index_prefer_the_longer_match()
    {
        // Both patterns start at the same index; the longer (later-ending) match
        // is the more specific verdict and must win regardless of rule order.
        const string shortFirst =
            @"{""matchRules"":[{""pattern"":""approve"",""edgeName"":""Approve""},{""pattern"":""approve with nits"",""edgeName"":""ApproveWithNits""}]}";
        const string longFirst =
            @"{""matchRules"":[{""pattern"":""approve with nits"",""edgeName"":""ApproveWithNits""},{""pattern"":""approve"",""edgeName"":""Approve""}]}";
        var output = NodeExecutionResult.Ok("verdict: approve with nits");

        var a = Assert.IsType<NodeOutcome.Success>(await LastOutcomeAsync(shortFirst, output));
        var b = Assert.IsType<NodeOutcome.Success>(await LastOutcomeAsync(longFirst, output));

        Assert.Equal("ApproveWithNits", a.EdgeName);
        Assert.Equal("ApproveWithNits", b.EdgeName);
    }

    [Fact]
    public async Task Backreference_patterns_match_the_same_way_they_read()
    {
        // A backreference is applied left-to-right. (Scanning the pattern itself
        // backwards — RegexOptions.RightToLeft — would fail to match here, and
        // the rule would silently lose.)
        const string config =
            @"{""matchRules"":[{""pattern"":""(approve)\\s+\\1"",""edgeName"":""Approve""}]}";

        var outcome = await LastOutcomeAsync(config, NodeExecutionResult.Ok("approve approve"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal("Approve", success.EdgeName);
    }

    [Fact]
    public async Task Alternation_picks_the_branch_a_left_to_right_read_would()
    {
        // "approve|approve with nits" matches the FIRST viable branch, so the
        // match is the 7-char "approve" — that Length is what feeds the
        // tie-break, and the longer literal rule at the same index must win.
        const string config =
            @"{""matchRules"":[{""pattern"":""approve|approve with nits"",""edgeName"":""Short""},{""pattern"":""approve with nits"",""edgeName"":""Long""}]}";

        var outcome = await LastOutcomeAsync(config, NodeExecutionResult.Ok("verdict: approve with nits"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal("Long", success.EdgeName);
    }

    [Fact]
    public async Task An_unparseable_pattern_is_skipped_and_the_other_rules_still_route()
    {
        // Legacy configs predate save-time pattern validation. A malformed rule
        // must not take down routing that used to work: it just never matches.
        const string config =
            @"{""matchRules"":[{""pattern"":""approve"",""edgeName"":""Approve""},{""pattern"":""[unclosed"",""edgeName"":""Broken""}]}";

        var outcome = await LastOutcomeAsync(config, NodeExecutionResult.Ok("verdict: approve"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.Custom, success.Edge);
        Assert.Equal("Approve", success.EdgeName);
    }

    [Fact]
    public async Task An_output_matching_only_an_unparseable_pattern_falls_through_to_OnSuccess()
    {
        var outcome = await LastOutcomeAsync(
            @"{""matchRules"":[{""pattern"":""[unclosed"",""edgeName"":""Broken""}]}",
            NodeExecutionResult.Ok("verdict: approve"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.OnSuccess, success.Edge);
        Assert.Null(success.EdgeName);
    }

    [Fact]
    public async Task Blank_and_edgeless_rules_are_skipped()
    {
        // A rule missing either half is unroutable and must not swallow the
        // match, even though its (empty) pattern would match anywhere.
        const string config =
            @"{""matchRules"":[{""pattern"":"""",""edgeName"":""Blank""},{""pattern"":""approve"",""edgeName"":""""},{""pattern"":""reject"",""edgeName"":""Reject""}]}";

        var outcome = await LastOutcomeAsync(config, NodeExecutionResult.Ok("reject then approve"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal("Reject", success.EdgeName);
    }

    [Fact]
    public async Task Non_matching_output_falls_through_to_OnSuccess()
    {
        var outcome = await LastOutcomeAsync(TwoRuleConfig, NodeExecutionResult.Ok("all good, shipping it"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.OnSuccess, success.Edge);
        Assert.Null(success.EdgeName);
    }

    [Fact]
    public async Task Adapter_failure_routes_to_OnFailure()
    {
        var outcome = await LastOutcomeAsync(TwoRuleConfig, NodeExecutionResult.Fail("adapter blew up"));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcome);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
    }

    [Fact]
    public async Task Empty_aiProviderId_uses_default_provider()
    {
        var defaultProvider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync(defaultProvider);

        var sp = BuildServices(providerStore.Object);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(@"{}"), MakeRun(), sp);

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(ctx))
            outcomes.Add(o);

        // Should not fail with "missing aiProviderId"
        Assert.DoesNotContain(outcomes, o =>
            o is NodeOutcome.Fail f && f.Reason.Contains("aiProviderId"));

        // Verify the default provider was looked up
        providerStore.Verify(s => s.GetDefaultAiProviderAsync(), Times.Once);
    }

    [Fact]
    public async Task Null_aiProviderId_and_no_default_provider_yields_descriptive_fail()
    {
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync((AiProvider?)null);

        var sp = BuildServices(providerStore.Object);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(null), MakeRun(), sp);

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(ctx))
            outcomes.Add(o);

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
        Assert.Contains("no default provider", fail.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_aiProviderId_not_found_yields_fail()
    {
        var providerId = Guid.NewGuid();
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByIdAsync(providerId))
            .ReturnsAsync((AiProvider?)null);

        var sp = BuildServices(providerStore.Object);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(
            MakeNode($@"{{""aiProviderId"":""{providerId}""}}"),
            MakeRun(), sp);

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(ctx))
            outcomes.Add(o);

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
        Assert.Contains(providerId.ToString(), fail.Reason);
        // The default-provider path must NOT be called
        providerStore.Verify(s => s.GetDefaultAiProviderAsync(), Times.Never);
    }

    // ── Work-item AI provider override ──────────────────────────────────────

    private static AiProvider Provider(string name, bool isDefault = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "stub",
        IsDefault = isDefault,
        Parallelism = 1,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// A registry that records the provider the executor resolved an adapter for,
    /// so a test can assert which provider actually won after any override.
    /// </summary>
    private static (IAgentAdapterRegistry registry, Func<AiProvider?> resolved) CapturingRegistry()
    {
        var adapter = new StubAdapter(NodeExecutionResult.Ok("done"));
        AiProvider? captured = null;
        var reg = new Mock<IAgentAdapterRegistry>();
        reg.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns((AiProvider p) => { captured = p; return () => adapter; });
        return (reg.Object, () => captured);
    }

    private static (Mock<IProviderStore> store, AiProvider def, AiProvider pinned, AiProvider ovr) BuildOverrideProviderStore()
    {
        var def = Provider("default", isDefault: true);
        var pinned = Provider("pinned");
        var ovr = Provider("override");
        var store = new Mock<IProviderStore>();
        store.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(def);
        store.Setup(s => s.GetAiProviderByIdAsync(def.Id)).ReturnsAsync(def);
        store.Setup(s => s.GetAiProviderByIdAsync(pinned.Id)).ReturnsAsync(pinned);
        store.Setup(s => s.GetAiProviderByIdAsync(ovr.Id)).ReturnsAsync(ovr);
        return (store, def, pinned, ovr);
    }

    private async Task<(AiProvider? resolved, NodeOutcome last)> RunWithOverrideAsync(
        Mock<IProviderStore> store, string? nodeConfig, WorkItemView workItem)
    {
        var (registry, resolved) = CapturingRegistry();
        var sp = BuildServices(store.Object, registry: registry, workItem: workItem);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(nodeConfig), MakeRun(), sp);

        NodeOutcome? last = null;
        await foreach (var o in executor.ExecuteAsync(ctx))
            last = o;
        return (resolved(), last!);
    }

    private static WorkItemView WorkItem(RemoteAiProviderOverrideMode mode, Guid? overrideId) => new()
    {
        Id = "WI-1",
        AiProviderOverride = mode,
        AiProviderOverrideId = overrideId,
    };

    [Fact]
    public async Task OverrideAll_replaces_even_a_node_pinned_to_a_specific_provider()
    {
        var (store, _, pinned, ovr) = BuildOverrideProviderStore();
        var (resolved, _) = await RunWithOverrideAsync(
            store,
            $@"{{""aiProviderId"":""{pinned.Id}""}}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, ovr.Id));

        Assert.Equal(ovr.Id, resolved!.Id);
    }

    [Fact]
    public async Task OverrideDefault_leaves_a_node_pinned_to_a_specific_provider_alone()
    {
        var (store, _, pinned, ovr) = BuildOverrideProviderStore();
        var (resolved, _) = await RunWithOverrideAsync(
            store,
            $@"{{""aiProviderId"":""{pinned.Id}""}}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideDefault, ovr.Id));

        // The node deliberately pinned a provider, so OverrideDefault must not touch it.
        Assert.Equal(pinned.Id, resolved!.Id);
    }

    [Fact]
    public async Task OverrideDefault_replaces_a_node_that_fell_back_to_the_default()
    {
        var (store, _, _, ovr) = BuildOverrideProviderStore();
        var (resolved, _) = await RunWithOverrideAsync(
            store,
            @"{}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideDefault, ovr.Id));

        Assert.Equal(ovr.Id, resolved!.Id);
    }

    [Fact]
    public async Task None_mode_never_overrides_even_with_a_target_set()
    {
        var (store, def, _, ovr) = BuildOverrideProviderStore();
        var (resolved, _) = await RunWithOverrideAsync(
            store,
            @"{}",
            WorkItem(RemoteAiProviderOverrideMode.None, ovr.Id));

        Assert.Equal(def.Id, resolved!.Id);
    }

    [Fact]
    public async Task Override_without_a_target_provider_is_a_no_op()
    {
        var (store, def, _, _) = BuildOverrideProviderStore();
        var (resolved, _) = await RunWithOverrideAsync(
            store,
            @"{}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, overrideId: null));

        Assert.Equal(def.Id, resolved!.Id);
    }

    [Fact]
    public async Task Override_target_provider_not_found_fails_the_node()
    {
        var (store, _, _, _) = BuildOverrideProviderStore();
        var missingId = Guid.NewGuid();
        var (_, last) = await RunWithOverrideAsync(
            store,
            @"{}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, missingId));

        var fail = Assert.IsType<NodeOutcome.Fail>(last);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains(missingId.ToString(), fail.Reason);
    }

    // ── Concurrency is claimed against the post-override provider ───────────
    //
    // These pin the layer that is already correct: whatever the node pins, the
    // slot is taken from the provider the override actually routes to. They are
    // the backstop that keeps RemoteWorkItemCoordinator's wrong-provider resume
    // peek from becoming a real parallelism breach — so a fix there must not
    // change what happens here.

    private async Task<(AiProvider? resolved, NodeOutcome last)> RunWithConcurrencyAsync(
        Mock<IProviderStore> store, string? nodeConfig, WorkItemView workItem,
        IAiProviderConcurrencyTracker concurrency)
    {
        var (registry, resolved) = CapturingRegistry();
        var sp = BuildServices(store.Object, registry: registry, workItem: workItem, concurrency: concurrency);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(nodeConfig), MakeRun(), sp);

        NodeOutcome? last = null;
        await foreach (var o in executor.ExecuteAsync(ctx))
            last = o;
        return (resolved(), last!);
    }

    [Fact]
    public async Task Waits_when_the_override_target_is_at_capacity_though_the_pinned_provider_is_free()
    {
        var (store, _, pinned, ovr) = BuildOverrideProviderStore();
        var tracker = new AiProviderConcurrencyTracker();
        Assert.True(tracker.TryEnter(ovr.Id, ovr.Parallelism)); // fill the override's only slot

        var (_, last) = await RunWithConcurrencyAsync(
            store,
            $@"{{""aiProviderId"":""{pinned.Id}""}}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, ovr.Id),
            tracker);

        var waiting = Assert.IsType<NodeOutcome.WaitingIld>(last);
        Assert.Contains(ovr.Name, waiting.Reason);
        // The pinned provider was never entered — it is not the gate.
        Assert.Equal(0, tracker.ActiveCount(pinned.Id));
    }

    [Fact]
    public async Task Runs_when_the_override_target_is_free_though_the_pinned_provider_is_at_capacity()
    {
        var (store, _, pinned, ovr) = BuildOverrideProviderStore();
        var tracker = new AiProviderConcurrencyTracker();
        Assert.True(tracker.TryEnter(pinned.Id, pinned.Parallelism)); // saturate the pinned provider

        var (resolved, last) = await RunWithConcurrencyAsync(
            store,
            $@"{{""aiProviderId"":""{pinned.Id}""}}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, ovr.Id),
            tracker);

        Assert.IsNotType<NodeOutcome.WaitingIld>(last);
        Assert.Equal(ovr.Id, resolved!.Id);
        // The override's slot was claimed and released around the adapter call.
        Assert.Equal(0, tracker.ActiveCount(ovr.Id));
    }
}
