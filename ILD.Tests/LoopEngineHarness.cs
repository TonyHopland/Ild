using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Minimal in-process harness for driving <see cref="LoopEngine"/> against a
/// SQLite-backed AppDbContext with scripted executor outcomes. The engine's
/// background <c>Task.Run</c> loop is bypassed: tests invoke <see cref="RunAsync"/>
/// to step the engine synchronously via reflection on the internal
/// <c>RunUntilParkAsync</c>.
/// </summary>
internal sealed class LoopEngineHarness : IDisposable
{
    public TestDb Db { get; }
    public Mock<IWorkItemManager> WorkItemsMock { get; }
    public Mock<IWorkItemNotifier> WorkItemNotifierMock { get; }
    public ScriptedExecutorRegistry Registry { get; }
    public ILoopEngine Engine { get; }
    public IServiceProvider Services { get; }
    public Guid TemplateVersionId { get; }
    public Dictionary<string, LoopNode> NodesById { get; } = new();
    public Guid RunId { get; private set; }
    public string WorkItemId { get; } = $"WI-{Guid.NewGuid():N}";

    private readonly ServiceProvider _sp;
    private readonly LoopEngine _engine;

    public LoopEngineHarness()
    {
        Db = new TestDb();

        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        TemplateVersionId = version.Id;
        Db.Context.LoopTemplates.Add(template);
        Db.Context.LoopTemplateVersions.Add(version);
        Db.Context.SaveChanges();

        WorkItemsMock = new Mock<IWorkItemManager>(MockBehavior.Loose);
        WorkItemsMock.Setup(m => m.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new WorkItemView { Id = id, RepositoryId = null });
        WorkItemsMock.Setup(m => m.TransitionAsync(
                It.IsAny<string>(),
                It.IsAny<ILD.Core.Services.Remote.RemoteWorkItemStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        WorkItemNotifierMock = new Mock<IWorkItemNotifier>(MockBehavior.Loose);

        Registry = new ScriptedExecutorRegistry();

        var services = new ServiceCollection();
        services.AddSingleton(Db.Context);
        services.AddSingleton<ILoopRunStore>(Db.LoopRuns);
        services.AddSingleton<ILoopTemplateStore>(Db.LoopTemplates);
        services.AddSingleton<IEventLogStore>(Db.EventLogs);
        services.AddSingleton<IRunNotifier, NoopRunNotifier>();
        services.AddSingleton<IWorkItemManager>(WorkItemsMock.Object);
        services.AddSingleton<IWorkItemNotifier>(WorkItemNotifierMock.Object);
        services.AddSingleton<INodeExecutorRegistry>(Registry);
        services.AddSingleton<ILoopEngine>(sp =>
        {
            return new LoopEngine(sp, Registry, sp.GetRequiredService<IRunNotifier>(),
                NullLogger<LoopEngine>.Instance, sp.GetRequiredService<IWorkItemNotifier>());
        });
        _sp = services.BuildServiceProvider();
        Services = _sp;
        Engine = _sp.GetRequiredService<ILoopEngine>();
        _engine = (LoopEngine)Engine;
    }

    public LoopNode AddNode(string key, NodeType type, string label = "")
    {
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = TemplateVersionId,
            NodeType = type,
            Label = string.IsNullOrEmpty(label) ? key : label,
            Config = null,
        };
        NodesById[key] = node;
        Db.Context.LoopNodes.Add(node);
        Db.Context.SaveChanges();
        return node;
    }

    public void AddEdge(string from, string to, EdgeType type, string? name = null)
    {
        Db.Context.LoopNodeEdges.Add(new LoopNodeEdge
        {
            Id = Guid.NewGuid(),
            SourceNodeId = NodesById[from].Id,
            TargetNodeId = NodesById[to].Id,
            EdgeType = type,
            Name = name,
        });
        Db.Context.SaveChanges();
    }

    public LoopRun SeedRun(string startNodeKey, LoopRunStatus status = LoopRunStatus.Running)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = WorkItemId,
            LoopTemplateVersionId = TemplateVersionId,
            Status = status,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = NodesById[startNodeKey].Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        Db.Context.LoopRuns.Add(run);
        Db.Context.SaveChanges();
        RunId = run.Id;
        return run;
    }

    /// <summary>Drives the engine inline until it parks. Returns when no node is currently executing.</summary>
    public async Task RunAsync()
    {
        var method = typeof(LoopEngine).GetMethod("RunUntilParkAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var task = (Task)method.Invoke(_engine, new object[] { RunId, CancellationToken.None })!;
        await task;
    }

    public LoopRun ReloadRun()
        => Db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == RunId);

    public IReadOnlyList<LoopRunNode> ReloadRunNodes()
        => Db.Fresh().LoopRunNodes.AsNoTracking()
            .Where(rn => rn.LoopRunId == RunId)
            .OrderBy(rn => rn.StartedAt)
            .ToList();

    public void Dispose()
    {
        _sp.Dispose();
        Db.Dispose();
    }
}

internal sealed class ScriptedExecutorRegistry : INodeExecutorRegistry
{
    private readonly Dictionary<NodeType, INodeExecutor> _byType = new();
    public void Register(INodeExecutor exec) => _byType[exec.NodeType] = exec;
    public INodeExecutor Get(NodeType type) => _byType.TryGetValue(type, out var e)
        ? e
        : throw new InvalidOperationException($"No executor registered for {type}");
}

/// <summary>Emits a fixed sequence of NodeOutcome values each time the engine
/// invokes <see cref="ExecuteAsync"/>. The script is per-invocation: if the node
/// is re-entered (e.g. after a human signal) the next entry in <see cref="Scripts"/>
/// is used.</summary>
internal sealed class ScriptedExecutor : INodeExecutor
{
    public NodeType NodeType { get; }
    public Queue<NodeOutcome[]> Scripts { get; } = new();
    public int Invocations { get; private set; }

    public ScriptedExecutor(NodeType type, params NodeOutcome[] outcomes)
    {
        NodeType = type;
        if (outcomes.Length > 0) Scripts.Enqueue(outcomes);
    }

    public ScriptedExecutor Then(params NodeOutcome[] outcomes)
    {
        Scripts.Enqueue(outcomes);
        return this;
    }

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        Invocations++;
        var script = Scripts.Count > 0 ? Scripts.Dequeue() : Array.Empty<NodeOutcome>();
        foreach (var o in script)
        {
            await Task.Yield();
            yield return o;
        }
    }
}
