using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
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
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
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

    // Matching sets no RegexOptions beyond IgnoreCase, so ^ and $ bind to the
    // whole output rather than to a line. These two pin the anchoring advice in
    // the Chat Context loop authoring guide (ChatService.LoopAuthoringGuide) to
    // what SelectLastMatchingRule actually does: the guide recommends (?m)
    // precisely because the bare anchors below misroute in silence.
    [Fact]
    public async Task A_bare_anchored_verdict_never_fires_on_a_narrated_output()
    {
        const string config =
            @"{""matchRules"":[{""pattern"":""^TO_REVIEW$"",""edgeName"":""Review""}]}";

        var outcome = await LastOutcomeAsync(
            config, NodeExecutionResult.Ok("Tests pass and the diff is small.\nTO_REVIEW"));

        // No match, so the node falls through to OnSuccess and the Review edge is
        // never taken — the failure an author would otherwise debug at run time.
        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.OnSuccess, success.Edge);
        Assert.Null(success.EdgeName);
    }

    [Fact]
    public async Task An_inline_multiline_anchored_verdict_routes_on_the_closing_line()
    {
        const string config =
            @"{""matchRules"":[{""pattern"":""(?m)^TO_REVIEW$"",""edgeName"":""Review""}]}";

        var outcome = await LastOutcomeAsync(
            config, NodeExecutionResult.Ok("Tests pass and the diff is small.\nTO_REVIEW"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.Custom, success.Edge);
        Assert.Equal("Review", success.EdgeName);
    }

    [Fact]
    public async Task An_unanchored_token_routes_on_a_passing_mention_when_nothing_matches_later()
    {
        // The other half of the guide's anchoring advice: last-match-wins protects
        // an unanchored token only when a genuine verdict matches later. With no
        // such rule the incidental mention is the last — and only — match, so it
        // routes. This is why the guide calls unanchored tokens the riskier form.
        const string config =
            @"{""matchRules"":[{""pattern"":""TO_REVIEW"",""edgeName"":""Review""}]}";

        var outcome = await LastOutcomeAsync(
            config, NodeExecutionResult.Ok("This is not a TO_REVIEW situation; parking for a human."));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.Custom, success.Edge);
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

    // ── Provider resolution by tag ──────────────────────────────────────────

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

    private static async Task<(AiProvider? resolved, NodeOutcome last)> RunAsync(
        IProviderStore store, string? nodeConfig, WorkItemView? workItem = null,
        IAiProviderConcurrencyTracker? concurrency = null)
    {
        var (registry, resolved) = CapturingRegistry();
        var sp = BuildServices(store, registry: registry, workItem: workItem, concurrency: concurrency);
        var ctx = BuildCtx(MakeNode(nodeConfig), MakeRun(), sp);

        NodeOutcome? last = null;
        await foreach (var o in new AINodeExecutor().ExecuteAsync(ctx))
            last = o;
        return (resolved(), last!);
    }

    private static WorkItemView WorkItem(RemoteAiProviderOverrideMode mode, Guid? overrideId) => new()
    {
        Id = "WI-1",
        AiProviderOverride = mode,
        AiProviderOverrideId = overrideId,
    };

    private static AiProvider Provider(string name, bool isDefault = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "stub",
        Model = "m",
        IsDefault = isDefault,
        Parallelism = 1,
        CreatedAt = DateTime.UtcNow,
    };

    [Theory]
    [MemberData(nameof(AiNodeProviderResolutionScenarios.Cases), MemberType = typeof(AiNodeProviderResolutionScenarios))]
    public async Task Runs_on_the_provider_the_tag_default_and_override_rules_pick(
        string nodeConfig, RemoteAiProviderOverrideMode mode, bool overrideTargetSet, string expected)
    {
        using var db = new TestDb();
        var seeded = await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers);

        var (resolved, last) = await RunAsync(
            db.Providers,
            seeded.Expand(nodeConfig),
            WorkItem(mode, overrideTargetSet ? seeded.Bravo.Id : null));

        Assert.IsType<NodeOutcome.Success>(last);
        Assert.Equal(expected, resolved?.Name);
    }

    [Theory]
    [MemberData(nameof(AiNodeProviderResolutionScenarios.NoDefaultCases), MemberType = typeof(AiNodeProviderResolutionScenarios))]
    public async Task With_no_default_provider_an_applicable_override_still_picks_the_provider(
        string nodeConfig, RemoteAiProviderOverrideMode mode, bool overrideTargetSet, string expected)
    {
        using var db = new TestDb();
        var seeded = await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers, withDefault: false);
        var workItem = WorkItem(mode, overrideTargetSet ? seeded.Bravo.Id : null);
        var target = seeded.ByName(expected);

        var (resolved, last) = await RunAsync(db.Providers, nodeConfig, workItem, new AiProviderConcurrencyTracker());

        Assert.IsType<NodeOutcome.Success>(last);
        Assert.Equal(expected, resolved?.Name);

        // The slot claimed is the chosen provider's: with it full, the node waits.
        var targetFull = new AiProviderConcurrencyTracker();
        Assert.True(targetFull.TryEnter(target.Id, target.Parallelism));
        var (_, waited) = await RunAsync(db.Providers, nodeConfig, workItem, targetFull);
        var waiting = Assert.IsType<NodeOutcome.WaitingIld>(waited);
        Assert.Contains(expected, waiting.Reason);
    }

    [Theory]
    [InlineData(@"{""aiProviderTag"":""Nightly""}", RemoteAiProviderOverrideMode.OverrideAll, "Nightly")]
    [InlineData(@"{""aiProviderTag"":""Nightly""}", RemoteAiProviderOverrideMode.OverrideDefault, "Nightly")]
    [InlineData(@"{}", RemoteAiProviderOverrideMode.OverrideDefault, null)]
    public async Task With_no_default_provider_an_override_mode_without_a_target_still_fails_saying_so(
        string nodeConfig, RemoteAiProviderOverrideMode mode, string? namedTag)
    {
        using var db = new TestDb();
        await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers, withDefault: false);

        var (resolved, last) = await RunAsync(db.Providers, nodeConfig, WorkItem(mode, null));

        Assert.Null(resolved);
        var fail = Assert.IsType<NodeOutcome.Fail>(last);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("no default provider", fail.Reason, StringComparison.OrdinalIgnoreCase);
        if (namedTag is not null)
            Assert.Contains(namedTag, fail.Reason);
    }

    [Fact]
    public async Task With_no_default_provider_a_missing_override_target_fails_naming_it()
    {
        using var db = new TestDb();
        await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers, withDefault: false);
        var missingId = Guid.NewGuid();

        var (_, last) = await RunAsync(
            db.Providers, @"{""aiProviderTag"":""Nightly""}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, missingId));

        var fail = Assert.IsType<NodeOutcome.Fail>(last);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains(missingId.ToString(), fail.Reason);
    }

    [Theory]
    [InlineData(@"{}", null)]
    [InlineData(@"{""aiProviderTag"":""  ""}", null)]
    [InlineData(@"{""aiProviderTag"":""Nightly""}", "Nightly")]
    public async Task No_matching_tag_and_no_default_provider_fails_saying_so(string nodeConfig, string? namedTag)
    {
        using var db = new TestDb();
        // A provider exists but is neither the default nor holds the tag.
        await db.Providers.CreateAiProviderAsync(Provider("other"), ["QA"]);

        var (resolved, last) = await RunAsync(db.Providers, nodeConfig);

        Assert.Null(resolved);
        var fail = Assert.IsType<NodeOutcome.Fail>(last);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("no default provider", fail.Reason, StringComparison.OrdinalIgnoreCase);
        if (namedTag is not null)
            Assert.Contains(namedTag, fail.Reason);
    }

    [Fact]
    public async Task A_legacy_provider_id_does_not_rescue_a_node_when_there_is_no_default()
    {
        using var db = new TestDb();
        var legacy = Provider("legacy");
        await db.Providers.CreateAiProviderAsync(legacy);

        var (resolved, last) = await RunAsync(db.Providers, $@"{{""aiProviderId"":""{legacy.Id}""}}");

        Assert.Null(resolved);
        var fail = Assert.IsType<NodeOutcome.Fail>(last);
        Assert.Contains("no default provider", fail.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Override_target_provider_not_found_fails_the_node()
    {
        using var db = new TestDb();
        await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers);
        var missingId = Guid.NewGuid();

        var (_, last) = await RunAsync(
            db.Providers, @"{}", WorkItem(RemoteAiProviderOverrideMode.OverrideAll, missingId));

        var fail = Assert.IsType<NodeOutcome.Fail>(last);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains(missingId.ToString(), fail.Reason);
    }

    [Fact]
    public async Task A_tag_moved_or_removed_between_runs_takes_effect_on_the_next_run()
    {
        using var db = new TestDb();
        var dflt = Provider("dflt", isDefault: true);
        var first = Provider("first");
        var second = Provider("second");
        await db.Providers.CreateAiProviderAsync(dflt);
        await db.Providers.CreateAiProviderAsync(first, ["Fast"]);
        await db.Providers.CreateAiProviderAsync(second);
        const string config = @"{""aiProviderTag"":""fast""}";

        Assert.Equal("first", (await RunAsync(db.Providers, config)).resolved?.Name);

        // The edit arrives the way the API makes it: another context, same database.
        await using (var api = db.Fresh())
        {
            var apiStore = new ProviderStore(api);
            var loaded = (await apiStore.GetAiProviderByIdAsync(second.Id))!;
            await apiStore.UpdateAiProviderAsync(loaded, ["FAST"]);
        }
        Assert.Equal("second", (await RunAsync(db.Providers, config)).resolved?.Name);

        await using (var api = db.Fresh())
        {
            var apiStore = new ProviderStore(api);
            var loaded = (await apiStore.GetAiProviderByIdAsync(second.Id))!;
            await apiStore.UpdateAiProviderAsync(loaded, []);
        }
        Assert.Equal("dflt", (await RunAsync(db.Providers, config)).resolved?.Name);
    }

    // ── Concurrency is claimed against the resolved provider ────────────────
    //
    // The slot is taken from the provider the node actually runs on — the tag's
    // holder, or the override target when the override applies. This is the
    // backstop behind RemoteWorkItemCoordinator's resume peek.

    [Fact]
    public async Task Waits_when_the_tagged_provider_is_at_capacity_though_the_default_is_free()
    {
        using var db = new TestDb();
        var seeded = await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers);
        var tracker = new AiProviderConcurrencyTracker();
        Assert.True(tracker.TryEnter(seeded.Alpha.Id, seeded.Alpha.Parallelism));

        var (_, last) = await RunAsync(
            db.Providers, @"{""aiProviderTag"":""QA""}", concurrency: tracker);

        var waiting = Assert.IsType<NodeOutcome.WaitingIld>(last);
        Assert.Contains(seeded.Alpha.Name, waiting.Reason);
        Assert.Equal(0, tracker.ActiveCount(seeded.Dflt.Id));
    }

    [Fact]
    public async Task Waits_when_the_override_target_is_at_capacity_though_the_tagged_provider_is_free()
    {
        using var db = new TestDb();
        var seeded = await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers);
        var tracker = new AiProviderConcurrencyTracker();
        Assert.True(tracker.TryEnter(seeded.Bravo.Id, seeded.Bravo.Parallelism));

        var (_, last) = await RunAsync(
            db.Providers, @"{""aiProviderTag"":""QA""}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, seeded.Bravo.Id), tracker);

        var waiting = Assert.IsType<NodeOutcome.WaitingIld>(last);
        Assert.Contains(seeded.Bravo.Name, waiting.Reason);
        Assert.Equal(0, tracker.ActiveCount(seeded.Alpha.Id));
    }

    [Fact]
    public async Task Runs_when_the_override_target_is_free_though_the_tagged_provider_is_at_capacity()
    {
        using var db = new TestDb();
        var seeded = await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers);
        var tracker = new AiProviderConcurrencyTracker();
        Assert.True(tracker.TryEnter(seeded.Alpha.Id, seeded.Alpha.Parallelism));

        var (resolved, last) = await RunAsync(
            db.Providers, @"{""aiProviderTag"":""QA""}",
            WorkItem(RemoteAiProviderOverrideMode.OverrideAll, seeded.Bravo.Id), tracker);

        Assert.IsNotType<NodeOutcome.WaitingIld>(last);
        Assert.Equal(seeded.Bravo.Id, resolved!.Id);
        // The override's slot was claimed and released around the adapter call.
        Assert.Equal(0, tracker.ActiveCount(seeded.Bravo.Id));
    }
}
