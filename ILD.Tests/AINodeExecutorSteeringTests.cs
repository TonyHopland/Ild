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
/// Covers the halt-and-steer one-shot injection the AI node executor performs:
/// when a resumed run carries a <c>SteeringNote</c>, the node continues the
/// captured live session (ignoring UseSession) with the note as the next
/// message, then clears the note so a later visit runs normally.
/// </summary>
public class AINodeExecutorSteeringTests
{
    private sealed class CapturingAdapter : IAgentAdapter
    {
        public AgentExecutionContext? Captured { get; private set; }
        public string Name => "Capturing";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Captured = context;
            return Task.FromResult(NodeExecutionResult.Ok("done", context.Prompt, context.SessionId));
        }
    }

    private sealed class FakeRegistry : IAgentAdapterRegistry
    {
        private readonly IAgentAdapter _adapter;
        public FakeRegistry(IAgentAdapter adapter) => _adapter = adapter;
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider) => () => _adapter;
        public string[] GetAllSupportedProviderTypes() => ["claude-code"];
    }

    private static (IServiceProvider sp, CapturingAdapter adapter) BuildServices(TestDb db, string workItemTitle = "")
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "claude-code",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);

        var wim = Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>())
                == Task.FromResult<WorkItemView?>(new WorkItemView { Id = "WI-1", Title = workItemTitle, RepositoryId = null }));

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(s => s.GetByRunIdAsync(It.IsAny<Guid>(), It.IsAny<int?>()))
            .ReturnsAsync(Array.Empty<EventLogEntry>());

        var adapter = new CapturingAdapter();
        var services = new ServiceCollection();
        services.AddSingleton(providerStore.Object);
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(wim);
        services.AddSingleton<IAgentAdapterRegistry>(new FakeRegistry(adapter));
        // Production always has the rendering service in scope, so the steering
        // path must be exercised with it present.
        services.AddSingleton<IPromptRenderingService>(new ILD.Core.Services.Implementations.PromptRenderingService(
            new ILD.Core.Services.Implementations.PromptTemplateResolver(), eventLog.Object, db.LoopRuns));
        return (services.BuildServiceProvider(), adapter);
    }

    private static LoopRun SeedRun(TestDb db, string? steeringNote, string? sessionId)
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
            SteeringNote = steeringNote,
            CurrentAiSessionId = sessionId,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    private static LoopNode AiNode(string configJson) => new()
    {
        Id = Guid.NewGuid(),
        NodeType = NodeType.AI,
        Config = configJson,
    };

    private static async Task DrainAsync(AINodeExecutor exec, NodeExecutionContext ctx)
    {
        await foreach (var _ in exec.ExecuteAsync(ctx)) { }
    }

    [Fact]
    public async Task Steering_note_forces_session_resume_and_uses_note_as_prompt()
    {
        using var db = new TestDb();
        var (sp, adapter) = BuildServices(db);
        // UseSession is false, yet steering must still resume the captured session.
        var run = SeedRun(db, steeringNote: "focus on the bug, ignore docs", sessionId: "sess-live-1");
        var node = AiNode("{\"useSession\":false,\"prompt\":\"original prompt\"}");
        var ctx = new NodeExecutionContext(run, node, sp, CancellationToken.None);

        await DrainAsync(new AINodeExecutor(), ctx);

        Assert.NotNull(adapter.Captured);
        Assert.Equal("sess-live-1", adapter.Captured!.SessionId);
        Assert.Equal("focus on the bug, ignore docs", adapter.Captured.Prompt);

        // The note is one-shot: cleared in the DB once consumed.
        var reloaded = db.Fresh().LoopRuns.First(r => r.Id == run.Id);
        Assert.Null(reloaded.SteeringNote);
    }

    [Fact]
    public async Task Empty_steering_note_falls_back_to_neutral_continue_message()
    {
        using var db = new TestDb();
        var (sp, adapter) = BuildServices(db);
        var run = SeedRun(db, steeringNote: "", sessionId: "sess-live-2");
        var node = AiNode("{\"useSession\":false,\"prompt\":\"original prompt\"}");
        var ctx = new NodeExecutionContext(run, node, sp, CancellationToken.None);

        await DrainAsync(new AINodeExecutor(), ctx);

        Assert.NotNull(adapter.Captured);
        Assert.Equal("sess-live-2", adapter.Captured!.SessionId);
        Assert.Equal("Continue where you left off.", adapter.Captured.Prompt);

        var reloaded = db.Fresh().LoopRuns.First(r => r.Id == run.Id);
        Assert.Null(reloaded.SteeringNote);
    }

    [Fact]
    public async Task Steering_note_reaches_the_agent_verbatim()
    {
        using var db = new TestDb();
        var (sp, adapter) = BuildServices(db, workItemTitle: "Fix the widget");
        // A steering note is the human's own words, not a template field: the
        // grammar they quote must survive, exactly as it does in a chat turn.
        const string note = "explain what {{WorkItem.Title}} and {{Var.handoff}} expand to";
        var run = SeedRun(db, steeringNote: note, sessionId: "sess-live-4");
        var node = AiNode("{\"useSession\":false,\"prompt\":\"{{WorkItem.Title}}\"}");
        var ctx = new NodeExecutionContext(run, node, sp, CancellationToken.None);

        await DrainAsync(new AINodeExecutor(), ctx);

        Assert.Equal(note, adapter.Captured!.Prompt);
        Assert.DoesNotContain("Fix the widget", adapter.Captured.Prompt);
    }

    [Fact]
    public async Task No_steering_note_renders_the_nodes_own_prompt_template()
    {
        using var db = new TestDb();
        var (sp, adapter) = BuildServices(db, workItemTitle: "Fix the widget");
        var run = SeedRun(db, steeringNote: null, sessionId: "sess-live-5");
        var node = AiNode("{\"useSession\":false,\"prompt\":\"Work on {{WorkItem.Title}}\"}");
        var ctx = new NodeExecutionContext(run, node, sp, CancellationToken.None);

        await DrainAsync(new AINodeExecutor(), ctx);

        // The configured prompt IS a template field — not rendering it would be
        // the opposite mistake.
        Assert.Equal("Work on Fix the widget", adapter.Captured!.Prompt);
    }

    [Fact]
    public async Task No_steering_note_runs_node_normally()
    {
        using var db = new TestDb();
        var (sp, adapter) = BuildServices(db);
        var run = SeedRun(db, steeringNote: null, sessionId: "sess-live-3");
        var node = AiNode("{\"useSession\":false,\"prompt\":\"original prompt\"}");
        var ctx = new NodeExecutionContext(run, node, sp, CancellationToken.None);

        await DrainAsync(new AINodeExecutor(), ctx);

        Assert.NotNull(adapter.Captured);
        // Without a steer, the node uses its configured prompt and does NOT
        // resume the captured session (UseSession is off).
        Assert.Equal("original prompt", adapter.Captured!.Prompt);
        Assert.Null(adapter.Captured.SessionId);
    }
}
