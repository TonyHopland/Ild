using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Resume of a failed AI attempt: the attempt keeps the agent's session id, Resume
/// continues that session with a short continuation prompt, and it falls back to a
/// fresh run when the session is gone.
/// </summary>
public class AiSessionResumeTests
{
    private static readonly AgentAdapterTraits CanResume = AgentAdapterTraits.None with { DisplayName = "Fake", SupportsSessionResume = true };
    private static readonly AgentAdapterTraits CannotResume = AgentAdapterTraits.None with { DisplayName = "Fake", SupportsSessionResume = false };

    private const string SessionGone = "exit=1 stderr=No conversation found with session ID: sess-old";

    /// <summary>An agent that reports its session, then fails or succeeds as scripted per call.</summary>
    private sealed class ScriptedAgent(string? reportedSessionId, params NodeExecutionResult[] results) : IAgentAdapter
    {
        private readonly Queue<NodeExecutionResult> _results = new(results);
        public List<AgentExecutionContext> Calls { get; } = new();
        public string Name => "Scripted";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public AgentAdapterTraits Traits => CanResume;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Calls.Add(context);
            if (reportedSessionId is not null) context.OnSessionId?.Invoke(reportedSessionId);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private static ServiceProvider ExecutorServices(TestDb db, IAgentAdapter adapter)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "default", Type = "claude-code", IsDefault = true, CreatedAt = DateTime.UtcNow };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);
        var services = new ServiceCollection();
        services.AddSingleton(NoPackageFeeds.Resolver);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>()) == Task.FromResult<WorkItemView?>(new WorkItemView { Id = "WI-1", Title = "T", RepositoryId = null })));
        services.AddSingleton<IAgentAdapterRegistry>(new SingleAdapterRegistry(adapter));
        return services.BuildServiceProvider();
    }

    private static (LoopRun run, LoopNode node, LoopRunNode visit) Seed(TestDb db, string? steeringNote = null, string? sessionId = null)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "WI-1", LoopTemplateVersionId = version.Id, Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume, SteeringNote = steeringNote, CurrentAiSessionId = sessionId,
        };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.AI, Config = "{\"prompt\":\"original prompt\"}" };
        var visit = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id, Status = LoopRunNodeStatus.Running, StartedAt = DateTime.UtcNow };
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.LoopRunNodes.Add(visit);
        db.Context.SaveChanges();
        return (run, node, visit);
    }

    private static async Task<List<NodeOutcome>> DrainAsync(NodeExecutionContext ctx)
    {
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new AINodeExecutor().ExecuteAsync(ctx)) outcomes.Add(o);
        return outcomes;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_session_id_is_recorded_on_the_attempt_whether_the_agent_succeeds_or_exits_non_zero(bool succeeds)
    {
        using var db = new TestDb();
        var agent = new ScriptedAgent("sess-1",
            succeeds ? NodeExecutionResult.Ok("done", "p", "sess-1") : NodeExecutionResult.Fail("exit=1 stderr=boom"));
        var (run, node, visit) = Seed(db);

        await DrainAsync(new NodeExecutionContext(run, node, ExecutorServices(db, agent), CancellationToken.None));

        Assert.Equal("sess-1", db.Fresh().LoopRunNodes.Single(n => n.Id == visit.Id).AiSessionId);
    }

    [Fact]
    public async Task Completing_the_attempt_does_not_write_a_stale_null_over_the_recorded_session_id()
    {
        using var db = new TestDb();
        var (run, node, visit) = Seed(db);
        var tracked = await db.LoopRuns.GetRunNodeByIdAsync(visit.Id);
        await db.LoopRuns.SetRunNodeAiSessionIdAsync(run.Id, node.Id, "sess-2");

        tracked!.Status = LoopRunNodeStatus.Failed;
        await db.LoopRuns.UpdateRunNodeAsync(tracked);

        var saved = db.Fresh().LoopRunNodes.Single(n => n.Id == visit.Id);
        Assert.Equal(LoopRunNodeStatus.Failed, saved.Status);
        Assert.Equal("sess-2", saved.AiSessionId);
    }

    [Fact]
    public async Task A_resume_sends_the_continuation_prompt_on_the_recorded_session_and_not_the_node_prompt()
    {
        using var db = new TestDb();
        const string note = "You were interrupted (reason: exit=1). Continue the task from where you left off.";
        var agent = new CapturingAdapter();
        var (run, node, _) = Seed(db, steeringNote: note, sessionId: "sess-9");

        await DrainAsync(new NodeExecutionContext(run, node, ExecutorServices(db, agent), CancellationToken.None));

        Assert.Equal("sess-9", agent.Captured!.SessionId);
        Assert.Equal("sess-9", agent.Captured.IncomingSessionId);
        Assert.Equal(note, agent.Captured.Prompt);
    }

    [Fact]
    public async Task A_resume_whose_session_is_gone_runs_the_node_from_the_beginning_and_says_so()
    {
        using var db = new TestDb();
        var agent = new ScriptedAgent(null, NodeExecutionResult.Fail(SessionGone), NodeExecutionResult.Ok("done", "p", "sess-new"));
        var (run, node, _) = Seed(db, steeringNote: "Continue the task.", sessionId: "sess-old");

        var outcomes = await DrainAsync(new NodeExecutionContext(run, node, ExecutorServices(db, agent), CancellationToken.None));

        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal("sess-old", agent.Calls[0].SessionId);
        Assert.Null(agent.Calls[1].SessionId);
        Assert.Equal("original prompt", agent.Calls[1].Prompt);
        var fellBack = Assert.Single(outcomes.OfType<NodeOutcome.ResumeFellBack>());
        Assert.Contains("No conversation found", fellBack.Reason);
        Assert.IsType<NodeOutcome.Success>(outcomes[^1]);
    }

    [Fact]
    public async Task A_resume_that_fails_for_another_reason_is_not_rerun()
    {
        using var db = new TestDb();
        var agent = new ScriptedAgent(null, NodeExecutionResult.Fail("exit=1 stderr=authentication failed"));
        var (run, node, _) = Seed(db, steeringNote: "Continue the task.", sessionId: "sess-old");

        var outcomes = await DrainAsync(new NodeExecutionContext(run, node, ExecutorServices(db, agent), CancellationToken.None));

        Assert.Single(agent.Calls);
        Assert.Empty(outcomes.OfType<NodeOutcome.ResumeFellBack>());
        Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
    }

    [Theory]
    [InlineData("No conversation found with session ID: 1234", true)]
    [InlineData("Session not found: abc", true)]
    [InlineData("error: no rollout found for thread id abc", true)]
    [InlineData("exit=1 stderr=boom", false)]
    [InlineData("exit=1 stderr= stdout=the session was not found in cache", false)]
    [InlineData(null, false)]
    public void The_classifier_recognises_a_session_that_is_gone(string? error, bool expected)
        => Assert.Equal(expected, AiFailureClassifier.IsSessionNotFound(error));

    // ---- engine ----

    private static LoopEngineHarness EngineWith(AgentAdapterTraits traits, out ScriptedExecutor agent, NodeOutcome[] first, NodeOutcome[]? second = null)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "default", Type = "claude-code", IsDefault = true, CreatedAt = DateTime.UtcNow };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);
        var h = new LoopEngineHarness(configure: s =>
        {
            s.AddSingleton(providerStore.Object);
            s.AddSingleton<IAgentAdapterRegistry>(new SingleAdapterRegistry(new CapturingAdapter { Traits = traits }));
        });
        h.AddNode("a", NodeType.AI, "Agent");
        agent = new ScriptedExecutor(NodeType.AI, first);
        if (second is not null) agent.Then(second);
        h.Registry.Register(agent);
        h.SeedRun("a");
        return h;
    }

    private static readonly NodeOutcome[] FailsAfterWork =
        [new NodeOutcome.NodeStarting("work"), new NodeOutcome.Fail(EdgeType.OnFailure, "exit=1 stderr=killed")];

    private static readonly NodeOutcome[] Finishes =
        [new NodeOutcome.NodeStarting("continue"), new NodeOutcome.Terminal("done")];

    private static void RecordSession(LoopEngineHarness h, string sessionId)
    {
        h.Db.Context.LoopRunNodes.Where(n => n.LoopRunId == h.RunId)
            .ExecuteUpdate(s => s.SetProperty(n => n.AiSessionId, sessionId));
        // The harness shares one tracking context across the engine and the test; reload what it holds.
        foreach (var entry in h.Db.Context.ChangeTracker.Entries<LoopRunNode>().ToList())
            entry.Reload();
    }

    [Fact]
    public async Task Resuming_a_failed_attempt_continues_its_session_with_a_continuation_prompt_and_keeps_the_failed_attempt()
    {
        using var h = EngineWith(CanResume, out var agent, FailsAfterWork, Finishes);
        await h.RunAsync();
        var failed = Assert.Single(h.ReloadRunNodes());
        RecordSession(h, "sess-7");
        Assert.Null(await h.Engine.GetResumeUnavailableReasonAsync(h.RunId, failed.Id));

        await h.Engine.ResumeFromNodeAsync(h.RunId, failed.Id);
        await h.WaitUntilIdleAsync();

        Assert.Equal("sess-7", agent.Seen[1].SessionId);
        Assert.Equal(
            "You were interrupted (reason: exit=1 stderr=killed). Continue the task from where you left off.",
            agent.Seen[1].SteeringNote);
        var visits = h.ReloadRunNodes();
        Assert.Equal(new[] { LoopRunNodeStatus.Failed, LoopRunNodeStatus.Succeeded }, visits.Select(n => n.Status));
        Assert.Equal("sess-7", visits[0].AiSessionId);
        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
    }

    [Fact]
    public async Task Resume_is_refused_with_a_reason_when_no_session_was_recorded()
    {
        using var h = EngineWith(CanResume, out _, FailsAfterWork);
        await h.RunAsync();
        var failed = Assert.Single(h.ReloadRunNodes());

        Assert.Equal("no session recorded", await h.Engine.GetResumeUnavailableReasonAsync(h.RunId, failed.Id));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.ResumeFromNodeAsync(h.RunId, failed.Id));
        Assert.Contains("no session recorded", ex.Message);
        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
    }

    [Fact]
    public async Task Resume_is_refused_with_a_reason_when_the_agent_cannot_resume_sessions()
    {
        using var h = EngineWith(CannotResume, out _, FailsAfterWork);
        await h.RunAsync();
        var failed = Assert.Single(h.ReloadRunNodes());
        RecordSession(h, "sess-7");

        Assert.Equal("Fake can't resume sessions", await h.Engine.GetResumeUnavailableReasonAsync(h.RunId, failed.Id));
    }

    [Fact]
    public async Task Resume_is_not_offered_on_a_node_that_is_not_an_AI_node()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("c", NodeType.Cmd, "Tests");
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd, FailsAfterWork));
        h.SeedRun("c");
        await h.RunAsync();
        var failed = Assert.Single(h.ReloadRunNodes());

        Assert.Equal("not an AI node", await h.Engine.GetResumeUnavailableReasonAsync(h.RunId, failed.Id));
    }
}
