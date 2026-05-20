using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// End-to-end coverage of the throttle → WaitingForIld → resume → Done flow
/// for two concurrent work items sharing a single-slot AI provider:
/// <list type="bullet">
///   <item>WI-A: Start → Human → AI → Cleanup. Hits Human first, parks in HumanFeedback.</item>
///   <item>WI-B: Start → AI → Cleanup. Takes the only AI slot.</item>
///   <item>Human feedback for WI-A unblocks it; AI slot is full → throttled → parks WaitingForIld.</item>
///   <item>WI-B finishes; AI slot frees; resuming WI-A runs the AI node and completes the run.</item>
/// </list>
/// Asserts: both runs end <c>Completed</c>, both work items end <c>Done</c>, and no
/// <c>LoopRunNode</c> is left in <c>Pending</c>, <c>Running</c>, or <c>WaitingHuman</c>
/// — in particular no "ghost" runnode from the throttle attempt.
/// </summary>
public sealed class LoopEngineWaitingForIldFlowTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _sp;
    private readonly LoopEngine _engine;
    private readonly IAiProviderConcurrencyTracker _tracker;
    private readonly AiProvider _aiProvider;
    private readonly LoopTemplate _templateA;
    private readonly LoopTemplate _templateB;
    private readonly LoopTemplateVersion _versionA;
    private readonly LoopTemplateVersion _versionB;
    private readonly Repository _repo;

    // WI-A graph
    private readonly LoopNode _aStart;
    private readonly LoopNode _aHuman;
    private readonly LoopNode _aAi;
    private readonly LoopNode _aCleanup;

    // WI-B graph
    private readonly LoopNode _bStart;
    private readonly LoopNode _bAi;
    private readonly LoopNode _bCleanup;

    private readonly TaskCompletionSource<NodeOutcome> _wiBAiGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly GatedAiExecutor _aiExec;

    public LoopEngineWaitingForIldFlowTests()
    {
        // ── Seed providers + repo ────────────────────────────────────────
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        _repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        _aiProvider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "single-slot",
            Type = "OpenCode",
            BaseUrl = "https://ai.example",
            Model = "m",
            Parallelism = 1,
        };

        _db.Context.RemoteProviders.Add(remote);
        _db.Context.Repositories.Add(_repo);
        _db.Context.AiProviders.Add(_aiProvider);

        // ── Templates ────────────────────────────────────────────────────
        _templateA = new LoopTemplate { Id = Guid.NewGuid(), Name = "tA", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 50 };
        _templateB = new LoopTemplate { Id = Guid.NewGuid(), Name = "tB", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 50 };
        _versionA = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = _templateA.Id, VersionNumber = 1 };
        _versionB = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = _templateB.Id, VersionNumber = 1 };
        _db.Context.LoopTemplates.AddRange(_templateA, _templateB);
        _db.Context.LoopTemplateVersions.AddRange(_versionA, _versionB);

        var aiConfig = $"{{\"AiProviderId\":\"{_aiProvider.Id}\"}}";

        _aStart = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = _versionA.Id, NodeType = NodeType.Start, Label = "A.start" };
        _aHuman = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = _versionA.Id, NodeType = NodeType.Human, Label = "A.human" };
        _aAi = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = _versionA.Id, NodeType = NodeType.AI, Label = "A.ai", Config = aiConfig };
        _aCleanup = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = _versionA.Id, NodeType = NodeType.Cleanup, Label = "A.cleanup" };

        _bStart = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = _versionB.Id, NodeType = NodeType.Start, Label = "B.start" };
        _bAi = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = _versionB.Id, NodeType = NodeType.AI, Label = "B.ai", Config = aiConfig };
        _bCleanup = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = _versionB.Id, NodeType = NodeType.Cleanup, Label = "B.cleanup" };

        _db.Context.LoopNodes.AddRange(_aStart, _aHuman, _aAi, _aCleanup, _bStart, _bAi, _bCleanup);
        _db.Context.LoopNodeEdges.AddRange(
            Edge(_aStart, _aHuman), Edge(_aHuman, _aAi), Edge(_aAi, _aCleanup),
            Edge(_bStart, _bAi), Edge(_bAi, _bCleanup));
        _db.Context.SaveChanges();

        // ── Executors ────────────────────────────────────────────────────
        // AI executor honours a shared concurrency tracker so WI-B holds the
        // sole slot while WI-A attempts to enter and gets throttled.
        _tracker = new AiProviderConcurrencyTracker();
        var startExec = new FakeExecutor(NodeType.Start);
        var humanExec = new FakeExecutor(NodeType.Human);
        var cleanupExec = new FakeExecutor(NodeType.Cleanup);
        _aiExec = new GatedAiExecutor(_tracker, _aiProvider, _wiBAiGate, slowNodeId: _bAi.Id);

        var registry = new NodeExecutorRegistry(new INodeExecutor[] { startExec, humanExec, _aiExec, cleanupExec });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INodeExecutorRegistry>(registry);
        services.AddSingleton<IRunNotifier, NoopRunNotifier>();
        services.AddSingleton<IWorkItemNotifier, NoopWorkItemNotifier>();
        services.AddSingleton<IActiveWorkItemTracker, InMemoryActiveWorkItemTracker>();
        services.AddSingleton(_tracker);
        services.AddSingleton<LoopEngine>();
        services.AddSingleton<ILoopRunStore>(_db.LoopRuns);
        services.AddSingleton<ILoopTemplateStore>(_db.LoopTemplates);
        services.AddSingleton<IProviderStore>(_db.Providers);
        services.AddSingleton<IEventLogStore>(_db.EventLogs);
        services.AddSingleton<IEventLogService>(new EventLogService(_db.EventLogs, _db.LoopRuns));
        services.AddSingleton<IRepositoryManager>(new Mock<IRepositoryManager>().Object);
        services.AddSingleton<IWorkItemServerClient>(_db.Server.Client);
        services.AddSingleton<IWorkItemServerOptionsResolver>(_db.Server.Options);
        services.AddSingleton<IWorkItemManager>(sp => new WorkItemManager(
            sp.GetRequiredService<IRepositoryManager>(),
            sp.GetRequiredService<IProviderStore>(),
            sp.GetRequiredService<IEventLogService>(),
            sp.GetRequiredService<ILoopRunStore>(),
            sp.GetRequiredService<IWorkItemServerClient>(),
            sp.GetRequiredService<IWorkItemServerOptionsResolver>(),
            sp.GetRequiredService<IWorkItemNotifier>()));

        _sp = services.BuildServiceProvider();
        _engine = _sp.GetRequiredService<LoopEngine>();
    }

    private static LoopNodeEdge Edge(LoopNode from, LoopNode to) => new()
    {
        Id = Guid.NewGuid(),
        SourceNodeId = from.Id,
        TargetNodeId = to.Id,
        EdgeType = EdgeType.OnSuccess,
    };

    [Fact]
    public async Task Throttled_AI_node_parks_in_WaitingForIld_and_resumes_cleanly_with_no_ghost_nodes()
    {
        // Seed a Running WI for each on the fake server + a LoopRun.
        var (wiA, runA) = await SeedWorkItemAndRunAsync(_versionA.Id);
        var (wiB, runB) = await SeedWorkItemAndRunAsync(_versionB.Id);

        // Drive WI-A first: Start → Human → suspends. Track via Engine.RunAsync.
        var aTask = _engine.RunAsync(runA);
        await aTask;

        // WI-A should now be parked at HumanFeedback waiting for input.
        var serverWiA = _db.Server.ServerDb.WorkItems.AsNoTracking().First(w => w.Id == wiA);
        Assert.Equal(ILD.WorkItemServer.Domain.WorkItemStatus.HumanFeedback, serverWiA.Status);
        var runADb = await _db.LoopRuns.GetByIdAsync(runA);
        Assert.Equal(LoopRunStatus.WaitingHuman, runADb!.Status);

        // Start WI-B in the background; its AI node takes the single slot.
        var bTask = Task.Run(() => _engine.RunAsync(runB));

        // Wait until WI-B's AI executor has entered the gate.
        await WaitUntilAsync(() => _tracker.ActiveCount(_aiProvider.Id) == 1);

        // Simulate human feedback for WI-A: mark the human runnode Succeeded and
        // set run back to Running, then re-enter via RunAsync (controller does this).
        await CompleteHumanNodeAsync(runA, _aHuman.Id);
        var aResume1 = _engine.RunAsync(runA);
        await aResume1;

        // After resume WI-A should have hit AI, found it full, and parked.
        var runAAfterThrottle = await _db.LoopRuns.GetByIdAsync(runA);
        Assert.Equal(LoopRunStatus.WaitingHuman, runAAfterThrottle!.Status);
        var serverWiAAfterThrottle = _db.Server.ServerDb.WorkItems.AsNoTracking().First(w => w.Id == wiA);
        Assert.Equal(ILD.WorkItemServer.Domain.WorkItemStatus.WaitingForIld, serverWiAAfterThrottle.Status);

        // No ghost LoopRunNode left in Pending/Running for WI-A's AI node.
        var nodesAfterThrottle = await _db.LoopRuns.GetRunNodesAsync(runA);
        Assert.DoesNotContain(nodesAfterThrottle, n =>
            n.LoopNodeId == _aAi.Id &&
            (n.Status == LoopRunNodeStatus.Running || n.Status == LoopRunNodeStatus.Pending));

        // Let WI-B's AI complete. Its run should finish.
        _wiBAiGate.SetResult(new NodeOutcome.Succeeded("b-ai-done"));
        await bTask;

        var runBAfter = await _db.LoopRuns.GetByIdAsync(runB);
        Assert.Equal(LoopRunStatus.Completed, runBAfter!.Status);
        var serverWiB = _db.Server.ServerDb.WorkItems.AsNoTracking().First(w => w.Id == wiB);
        Assert.Equal(ILD.WorkItemServer.Domain.WorkItemStatus.Done, serverWiB.Status);

        // Now resume WI-A (this is what RemoteWorkItemCoordinator does after the pulse).
        // The work item has to be transitioned back to Running on the server first.
        var mgr = _sp.GetRequiredService<IWorkItemManager>();
        await mgr.TransitionAsync(wiA, RemoteWorkItemStatus.Running);
        await _engine.RunAsync(runA);

        // ── Final assertions ─────────────────────────────────────────────
        var runAFinal = await _db.LoopRuns.GetByIdAsync(runA);
        Assert.Equal(LoopRunStatus.Completed, runAFinal!.Status);
        var serverWiAFinal = _db.Server.ServerDb.WorkItems.AsNoTracking().First(w => w.Id == wiA);
        Assert.Equal(ILD.WorkItemServer.Domain.WorkItemStatus.Done, serverWiAFinal.Status);

        // No LoopRunNode left in a non-terminal state across either run.
        var allNodesA = await _db.LoopRuns.GetRunNodesAsync(runA);
        var allNodesB = await _db.LoopRuns.GetRunNodesAsync(runB);
        Assert.All(allNodesA.Concat(allNodesB), n =>
            Assert.True(
                n.Status == LoopRunNodeStatus.Succeeded || n.Status == LoopRunNodeStatus.Failed || n.Status == LoopRunNodeStatus.Responded,
                $"Run node {n.Id} ({n.Status}) for LoopNodeId {n.LoopNodeId} is not in a terminal state"));

        // Tracker should be empty (no leaked AI slots).
        Assert.Equal(0, _tracker.ActiveCount(_aiProvider.Id));

        // The AI node on WI-A, when re-entered after throttle+resume, must see the
        // predecessor (Human) node's output as {{PreviousNode.Output}}. Without
        // the reexecute-current seeding fix it would observe null and downstream
        // prompt rendering would collapse to empty.
        Assert.Equal("ok", _aiExec.ObservedPrevOutputs[_aAi.Id]);
    }

    private async Task<(string workItemId, Guid runId)> SeedWorkItemAndRunAsync(Guid versionId)
    {
        var workItemId = Guid.NewGuid().ToString();
        _db.Server.ServerDb.WorkItems.Add(new ILD.WorkItemServer.Domain.WorkItem
        {
            Id = workItemId,
            Title = "wi",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = ILD.WorkItemServer.Domain.WorkItemStatus.Running,
        });
        await _db.Server.ServerDb.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = versionId,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.Running,
            RepositoryId = _repo.Id,
            StartedAt = DateTime.UtcNow,
        };
        _db.Context.LoopRuns.Add(run);
        await _db.Context.SaveChangesAsync();
        return (workItemId, run.Id);
    }

    private async Task CompleteHumanNodeAsync(Guid runId, Guid humanNodeId)
    {
        var nodes = await _db.LoopRuns.GetRunNodesAsync(runId);
        var rn = nodes.First(n => n.LoopNodeId == humanNodeId && n.Status == LoopRunNodeStatus.WaitingHuman);
        rn.Status = LoopRunNodeStatus.Succeeded;
        rn.Output = "ok";
        rn.CompletedAt = DateTime.UtcNow;
        await _db.LoopRuns.UpdateRunNodeAsync(rn);

        var run = await _db.LoopRuns.GetByIdAsync(runId);
        run!.Status = LoopRunStatus.Running;
        await _db.LoopRuns.UpdateRunAsync(run);

        // Mirror what WorkItemManager.SubmitHumanFeedbackInputAsync does.
        var mgr = _sp.GetRequiredService<IWorkItemManager>();
        var wi = await mgr.GetWorkItemAsync(run.WorkItemId);
        await mgr.TransitionAsync(wi!.Id, RemoteWorkItemStatus.Running);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition not reached within timeout");
    }

    public void Dispose()
    {
        // Drain any unfinished gates.
        _wiBAiGate.TrySetResult(new NodeOutcome.Failed("test teardown"));
        _sp.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// Fake AI node executor that honours a shared concurrency tracker and
    /// blocks on a TaskCompletionSource for a designated "slow" node so the
    /// test can hold the AI slot while another run attempts to enter.
    /// </summary>
    private sealed class GatedAiExecutor : INodeExecutor
    {
        private readonly IAiProviderConcurrencyTracker _tracker;
        private readonly AiProvider _provider;
        private readonly TaskCompletionSource<NodeOutcome> _slowGate;
        private readonly Guid _slowNodeId;
        public readonly Dictionary<Guid, string?> ObservedPrevOutputs = new();

        public GatedAiExecutor(IAiProviderConcurrencyTracker tracker, AiProvider provider, TaskCompletionSource<NodeOutcome> slowGate, Guid slowNodeId)
        {
            _tracker = tracker;
            _provider = provider;
            _slowGate = slowGate;
            _slowNodeId = slowNodeId;
        }

        public NodeType NodeType => NodeType.AI;

        public string DescribeInput(NodeExecutionContext ctx) => "ai";

        public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
        {
            if (!_tracker.TryEnter(_provider.Id, _provider.Parallelism))
                return new NodeOutcome.Throttled($"provider {_provider.Name} at capacity", _provider.Id);
            try
            {
                ObservedPrevOutputs[ctx.Node.Id] = ctx.PreviousNodeOutput;
                if (ctx.Node.Id == _slowNodeId)
                    return await _slowGate.Task;
                return new NodeOutcome.Succeeded("ai-ok");
            }
            finally
            {
                _tracker.Exit(_provider.Id);
            }
        }
    }
}
