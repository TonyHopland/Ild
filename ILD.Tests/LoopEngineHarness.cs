using System.Collections.Concurrent;
using System.Reflection;
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

    public LoopEngineHarness(IRunNotifier? notifier = null, IShutdownState? shutdown = null)
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
        // The engine resolves IEventLogService optionally; register it so node and
        // edge-traversal events are written exactly as they are in production.
        services.AddSingleton<IEventLogService>(new EventLogService(Db.EventLogs, Db.LoopRuns));
        services.AddSingleton<IRunNotifier>(notifier ?? new NoopRunNotifier());
        services.AddSingleton<IWorkItemManager>(WorkItemsMock.Object);
        services.AddSingleton<IWorkItemNotifier>(WorkItemNotifierMock.Object);
        services.AddSingleton<INodeExecutorRegistry>(Registry);
        services.AddSingleton<ILoopEngine>(sp =>
        {
            return new LoopEngine(sp, Registry, sp.GetRequiredService<IRunNotifier>(),
                NullLogger<LoopEngine>.Instance, sp.GetRequiredService<IWorkItemNotifier>(),
                progressBuffer: null, shutdown: shutdown);
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

    public LoopRun SeedRun(
        string startNodeKey,
        LoopRunStatus status = LoopRunStatus.Running,
        bool isHalted = false,
        HaltReason? haltReason = null)
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
            IsHalted = isHalted,
            HaltReason = haltReason,
        };
        Db.Context.LoopRuns.Add(run);
        Db.Context.SaveChanges();
        RunId = run.Id;
        return run;
    }

    /// <summary>
    /// Starts the run's real background driving loop — the one <see cref="RunAsync"/>
    /// deliberately bypasses. Everything keyed off run <i>ownership</i> (the launch
    /// gate, <see cref="ILoopEngine.GetActiveRunIdsAsync"/>, the shutdown drain)
    /// only sees a run that got here, because ownership is claimed inside the
    /// private <c>LaunchAsync</c> that <c>RunUntilParkAsync</c> is invoked beneath.
    /// Returns as soon as the loop is scheduled — pair it with a
    /// <see cref="BlockingExecutor"/> to know when the node is actually running.
    /// </summary>
    public Task LaunchAsync()
    {
        var method = typeof(LoopEngine).GetMethod("LaunchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(_engine, new object[] { RunId })!;
    }

    /// <summary>Drives the engine inline until it parks. Returns when no node is currently executing.</summary>
    public async Task RunAsync()
    {
        var method = typeof(LoopEngine).GetMethod("RunUntilParkAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(_engine, new object[] { RunId, CancellationToken.None })!;
        await task;
    }

    // The signal/retry resume paths do not drive the run inline: they commit the
    // resume and launch RunUntilParkAsync on a fire-and-forget Task.Run
    // (LaunchAfterAwaitAsync). That background drive keeps using this harness's
    // single shared SqliteConnection after the re-park is observable — it still
    // reads out-edges and transitions the work item (LoopEngine.RunUntilParkAsync,
    // after the status write). The engine tracks each run's live drive in the
    // private _runTasks map; awaiting the stored Task is the only way to know the
    // shared connection is quiescent, so we reach it the same way RunAsync reaches
    // RunUntilParkAsync — by reflection (the engine exposes no InternalsVisibleTo).
    private static readonly FieldInfo RunTasksField =
        typeof(LoopEngine).GetField("_runTasks", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Task? OutstandingDriveTask()
        => ((ConcurrentDictionary<Guid, Task>)RunTasksField.GetValue(_engine)!)
            .TryGetValue(RunId, out var t) ? t : null;

    /// <summary>
    /// Waits until this harness's run has no in-flight drive — i.e. any
    /// fire-and-forget resume launched by <see cref="ILoopEngine.SignalNodeResultAsync"/>
    /// or <see cref="ILoopEngine.RetryFromNodeAsync"/> has run to completion and the
    /// run has left <see cref="ILoopEngine.GetActiveRunIdsAsync"/>. After this returns
    /// the shared SQLite connection is quiescent, so signal-resume tests should await
    /// it before asserting or disposing rather than keying off the observable parked
    /// state alone (which becomes visible mid-drive, while post-park DB work is still
    /// running on the shared connection).
    /// </summary>
    public async Task WaitUntilIdleAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            // Await the actual drive Task (it completes cleanly — the engine catches
            // inside the loop), then re-check in case a relaunch chained another one.
            if (OutstandingDriveTask() is { IsCompleted: false } drive)
            {
                try { await drive.WaitAsync(limit - sw.Elapsed); } catch { /* drained or timed out */ }
                continue;
            }
            // No stored drive Task. If the run still owns a drive slot, a launch is
            // in flight but its Task handle isn't stored yet — spin briefly. Otherwise
            // the run is fully idle and the connection is safe to tear down.
            if (!(await Engine.GetActiveRunIdsAsync()).Contains(RunId))
                return;
            await Task.Delay(2);
        }
    }

    public LoopRun ReloadRun()
        => Db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == RunId);

    public IReadOnlyList<LoopRunNode> ReloadRunNodes()
        => Db.Fresh().LoopRunNodes.AsNoTracking()
            .Where(rn => rn.LoopRunId == RunId)
            .OrderBy(rn => rn.StartedAt)
            .ToList();

    /// <summary>All event-log rows written for this run, in sequence order.</summary>
    public IReadOnlyList<EventLog> ReloadEvents()
        => Db.Fresh().EventLogs.AsNoTracking()
            .Where(e => e.LoopRunId == RunId)
            .OrderBy(e => e.Sequence)
            .ToList();

    public void Dispose()
    {
        // Serialize teardown against any in-flight fire-and-forget drive before
        // disposing the shared SqliteConnection. Without this, disposing right
        // after a signal-resume re-park races SqliteConnection.Close() against the
        // drive's still-running post-park DB use — the intermittent CI teardown
        // fault (NullReferenceException out of Close()). Best-effort: a drain error
        // must never mask the real test outcome.
        try { WaitUntilIdleAsync().GetAwaiter().GetResult(); }
        catch { /* teardown drain is best-effort */ }
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

/// <summary>
/// A node that starts and then never finishes on its own — the shape of a live
/// AI node. It reports when the engine has entered it (after the
/// <see cref="NodeOutcome.NodeStarting"/> the engine turns into a Running
/// <c>LoopRunNode</c> row), then blocks on the run's cancellation token and lets
/// the <see cref="OperationCanceledException"/> out, exactly as a real executor
/// whose agent process was killed does.
/// </summary>
internal sealed class BlockingExecutor : INodeExecutor
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BlockingExecutor(NodeType type) => NodeType = type;

    public NodeType NodeType { get; }

    /// <summary>Completes once the node is running and the engine is waiting on it.</summary>
    public Task Entered => _entered.Task;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        yield return new NodeOutcome.NodeStarting("blocking");
        _entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
    }
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
