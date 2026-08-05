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
/// The routing half of the throttle park, at the AI node executor: a provider
/// interruption yields <see cref="NodeOutcome.Interrupted"/> (which the engine
/// parks for a human Resume) while a genuine failure still yields
/// <see cref="NodeOutcome.Fail"/> onto the <c>on_failure</c> edge, unchanged.
/// </summary>
public class AINodeExecutorThrottleParkTests
{
    private const string SessionLimitNotice = "You've hit your session limit · resets 9:40am (UTC)";

    /// <summary>
    /// Stands in for a CLI adapter: optionally reports a session id mid-stream
    /// (the real capture path, through <c>OnSessionId</c>) and then returns a
    /// scripted result. Records every context it saw so a resume can be checked.
    /// </summary>
    private sealed class ScriptedAdapter : IAgentAdapter
    {
        private readonly Queue<Func<AgentExecutionContext, NodeExecutionResult>> _script = new();
        public List<AgentExecutionContext> Calls { get; } = new();
        public string? SessionIdToReport { get; set; }

        public ScriptedAdapter(params Func<AgentExecutionContext, NodeExecutionResult>[] steps)
        {
            foreach (var step in steps) _script.Enqueue(step);
        }

        public string Name => "Scripted";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Calls.Add(context);
            if (SessionIdToReport is not null)
                context.OnSessionId?.Invoke(SessionIdToReport);
            return Task.FromResult(_script.Dequeue()(context));
        }
    }

    private sealed class FakeRegistry : IAgentAdapterRegistry
    {
        private readonly IAgentAdapter _adapter;
        public FakeRegistry(IAgentAdapter adapter) => _adapter = adapter;
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => _adapter;
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
    }

    private static IServiceProvider BuildServices(TestDb db, IAgentAdapter adapter)
    {
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "claude-code",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        });

        var wim = Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>())
                == Task.FromResult<WorkItemView?>(new WorkItemView { Id = "WI-1", RepositoryId = null }));

        var services = new ServiceCollection();
        services.AddSingleton(providerStore.Object);
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(wim);
        services.AddSingleton<IAgentAdapterRegistry>(new FakeRegistry(adapter));
        return services.BuildServiceProvider();
    }

    private static LoopRun SeedRun(TestDb db, string? sessionId = null, string? steeringNote = null)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            CurrentAiSessionId = sessionId,
            SteeringNote = steeringNote,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    private static LoopNode AiNode() => new()
    {
        Id = Guid.NewGuid(),
        NodeType = NodeType.AI,
        Config = "{\"prompt\":\"do the work\"}",
    };

    private static async Task<List<NodeOutcome>> RunAsync(IServiceProvider sp, LoopRun run)
    {
        var outcomes = new List<NodeOutcome>();
        var ctx = new NodeExecutionContext(run, AiNode(), sp, CancellationToken.None);
        await foreach (var outcome in new AINodeExecutor().ExecuteAsync(ctx))
            outcomes.Add(outcome);
        return outcomes;
    }

    /// <summary>The adapter shape all four CLIs produce on a non-zero exit.</summary>
    private static NodeExecutionResult ThrottledExit(string notice = SessionLimitNotice)
        => NodeExecutionResult.Fail("exit=1 stderr=", notice);

    [Fact]
    public async Task Throttle_with_a_captured_session_parks_for_a_same_session_resume()
    {
        using var db = new TestDb();
        var adapter = new ScriptedAdapter(_ => ThrottledExit()) { SessionIdToReport = "sess-live-1" };
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db);

        var outcomes = await RunAsync(sp, run);

        var parked = Assert.IsType<NodeOutcome.Interrupted>(outcomes.Last());
        // The adapter's raw output is what tells the human when the limit resets.
        Assert.Equal(SessionLimitNotice, parked.Output);
        Assert.Contains("continue the same agent session", parked.Reason);
        // The session id captured mid-stream is what Resume will continue from,
        // and the park read it back from the row rather than from its own
        // (pre-node, still null) copy of the run.
        Assert.Equal("sess-live-1", db.Fresh().LoopRuns.First(r => r.Id == run.Id).CurrentAiSessionId);
    }

    [Fact]
    public async Task Throttle_before_a_session_exists_parks_for_a_cold_restart()
    {
        using var db = new TestDb();
        // Throttled before OnSessionId ever fired: there is nothing to resume
        // into, but a throttle parks anyway — the value is control over WHEN the
        // next attempt fires, which survives losing the session.
        var adapter = new ScriptedAdapter(_ => ThrottledExit());
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db);

        var outcomes = await RunAsync(sp, run);

        var parked = Assert.IsType<NodeOutcome.Interrupted>(outcomes.Last());
        Assert.Contains("restart this node from the beginning", parked.Reason);
        Assert.Null(db.Fresh().LoopRuns.First(r => r.Id == run.Id).CurrentAiSessionId);
    }

    [Fact]
    public async Task Genuine_failure_still_takes_the_on_failure_edge()
    {
        using var db = new TestDb();
        var adapter = new ScriptedAdapter(_ => NodeExecutionResult.Fail(
            "exit=1 stderr=", "Invalid API key · Please run /login"));
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db);

        var outcomes = await RunAsync(sp, run);

        var failed = Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
        Assert.Equal(EdgeType.OnFailure, failed.Edge);
    }

    [Fact]
    public async Task Context_exhaustion_takes_the_on_failure_edge_rather_than_parking()
    {
        using var db = new TestDb();
        // Parking would only relocate the dead end: the resumed session hits the
        // same wall on its first turn.
        var adapter = new ScriptedAdapter(_ => ThrottledExit(
            "prompt is too long: 210000 tokens > 200000 maximum")) { SessionIdToReport = "sess-live-2" };
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db);

        var outcomes = await RunAsync(sp, run);

        Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
    }

    [Fact]
    public async Task An_adapter_classified_interruption_parks_without_the_text_classifier()
    {
        using var db = new TestDb();
        // opencode's `{"type":"error"}` path classifies from the structured event;
        // the failure text here says nothing a text classifier could use.
        var adapter = new ScriptedAdapter(_ => NodeExecutionResult.Fail(
            "opencode session error: provider said no", null, FailureKind.Interrupted));
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db);

        var outcomes = await RunAsync(sp, run);

        Assert.IsType<NodeOutcome.Interrupted>(outcomes.Last());
    }

    [Fact]
    public async Task An_adapter_classified_failure_routes_even_when_the_text_looks_throttled()
    {
        using var db = new TestDb();
        var adapter = new ScriptedAdapter(_ => NodeExecutionResult.Fail(
            "exit=1 stderr=", SessionLimitNotice, FailureKind.Failed));
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db);

        var outcomes = await RunAsync(sp, run);

        Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
    }

    [Fact]
    public async Task Resuming_a_no_session_park_re_runs_the_node_from_the_beginning()
    {
        using var db = new TestDb();
        var adapter = new ScriptedAdapter(_ => NodeExecutionResult.Ok("done"));
        var sp = BuildServices(db, adapter);
        // The shape a no-session park leaves behind: resumed (non-null note) but
        // with nothing to continue. "Continue where you left off." into a fresh
        // session would be a follow-up to a conversation that never happened, so
        // the node re-runs its own prompt instead — which is what the park's
        // wording promises.
        var run = SeedRun(db, sessionId: null, steeringNote: string.Empty);

        await RunAsync(sp, run);

        var call = Assert.Single(adapter.Calls);
        Assert.Equal("do the work", call.Prompt);
        Assert.Null(call.SessionId);
        Assert.Null(db.Fresh().LoopRuns.First(r => r.Id == run.Id).SteeringNote);
    }

    [Fact]
    public async Task A_cold_restart_keeps_the_humans_note_verbatim()
    {
        using var db = new TestDb();
        var adapter = new ScriptedAdapter(_ => NodeExecutionResult.Ok("done"));
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db, sessionId: null, steeringNote: "use {{the}} smaller model");

        await RunAsync(sp, run);

        // Appended after rendering: the note is the human's own words, not a
        // template field (ADR-0011).
        Assert.Equal("do the work\n\nuse {{the}} smaller model", Assert.Single(adapter.Calls).Prompt);
    }

    [Fact]
    public async Task Resuming_before_the_limit_resets_re_parks_with_the_session_intact()
    {
        using var db = new TestDb();
        // The whole workflow leans on a property that is accidental rather than
        // guaranteed: nothing in the codebase ever clears CurrentAiSessionId, so
        // resuming too early costs one wasted round-trip and re-parks the same
        // way — it does NOT degrade to the no-session park.
        var adapter = new ScriptedAdapter(_ => ThrottledExit(), _ => ThrottledExit())
        {
            SessionIdToReport = "sess-live-3",
        };
        var sp = BuildServices(db, adapter);
        var run = SeedRun(db);

        var firstPark = Assert.IsType<NodeOutcome.Interrupted>((await RunAsync(sp, run)).Last());
        Assert.Contains("continue the same agent session", firstPark.Reason);

        // Resume, as ResumeFromHaltAsync does: a non-null note is the flag that
        // makes the node continue the captured session.
        run.SteeringNote = string.Empty;
        await db.LoopRuns.UpdateRunAsync(run);

        // The impatient human's resume throttles again — this time the adapter
        // never reports a session id, because the provider stopped it before the
        // stream produced one.
        adapter.SessionIdToReport = null;
        var secondPark = Assert.IsType<NodeOutcome.Interrupted>((await RunAsync(sp, run)).Last());

        Assert.Equal("sess-live-3", db.Fresh().LoopRuns.First(r => r.Id == run.Id).CurrentAiSessionId);
        Assert.Contains("continue the same agent session", secondPark.Reason);
        // ...and the resumed turn really did continue that session.
        Assert.Equal("sess-live-3", adapter.Calls.Last().SessionId);
    }
}
