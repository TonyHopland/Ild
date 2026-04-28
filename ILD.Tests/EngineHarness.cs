using FluentAssertions;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// Engine-test scaffolding: builds a minimal DB graph and provides
/// a fake executor registry so tests can assert end-to-end engine behaviour.
/// </summary>
internal sealed class EngineHarness : IDisposable
{
    public TestDb Db { get; }
    public LoopEngine Engine { get; }
    public Dictionary<NodeType, FakeExecutor> Fakes { get; } = new();
    public Guid WorkItemId { get; private set; }
    public Guid RunId { get; private set; }
    public Dictionary<string, LoopNode> NodesById { get; } = new(); // user-supplied node label
    public Dictionary<string, LoopNodeEdge> EdgesById { get; } = new();

    public EngineHarness()
    {
        Db = new TestDb();

        foreach (var type in Enum.GetValues<NodeType>())
            Fakes[type] = new FakeExecutor(type);

        var registry = new NodeExecutorRegistry(Fakes.Values);
        Engine = new LoopEngine(() => Db.Fresh(), registry, new NoopRunNotifier());
    }

    public void BuildSimpleGraph(params (string id, NodeType type)[] nodes)
    {
        var remote = new ILD.Core.Models.RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new ILD.Core.Models.Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = "AutoResume", MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };

        Db.Context.RemoteProviders.Add(remote);
        Db.Context.Repositories.Add(repo);
        Db.Context.LoopTemplates.Add(template);
        Db.Context.LoopTemplateVersions.Add(version);

        foreach (var (id, type) in nodes)
        {
            var n = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = type, Label = id };
            NodesById[id] = n;
            Db.Context.LoopNodes.Add(n);
        }

        var wi = new WorkItem { Id = Guid.NewGuid(), Title = "wi", RepositoryId = repo.Id, LoopTemplateVersionId = version.Id, Status = WorkItemStatus.Running };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id, LoopTemplateVersionId = version.Id, RecoveryPolicy = "AutoResume", Status = LoopRunStatus.Running };
        Db.Context.WorkItems.Add(wi);
        Db.Context.LoopRuns.Add(run);
        WorkItemId = wi.Id;
        RunId = run.Id;
    }

    public void AddEdge(string id, string from, string to, EdgeType type = EdgeType.OnSuccess, int? maxTraversals = null)
    {
        var e = new LoopNodeEdge
        {
            Id = Guid.NewGuid(),
            SourceNodeId = NodesById[from].Id,
            TargetNodeId = NodesById[to].Id,
            EdgeType = type,
            MaxTraversals = maxTraversals,
        };
        EdgesById[id] = e;
        Db.Context.LoopNodeEdges.Add(e);
    }

    public void Save() => Db.Context.SaveChanges();

    public LoopRun ReloadRun() => Db.Fresh().LoopRuns.AsNoTracking().Include(r => r.RunNodes).First(r => r.Id == RunId);

    public IReadOnlyList<LoopRunNode> ReloadRunNodes()
        => Db.Fresh().LoopRunNodes.AsNoTracking().Where(rn => rn.LoopRunId == RunId).OrderBy(rn => rn.StartedAt).ToList();

    public WorkItem ReloadWorkItem() => Db.Fresh().WorkItems.AsNoTracking().First(w => w.Id == WorkItemId);

    public void Dispose() => Db.Dispose();
}

internal sealed class FakeExecutor : INodeExecutor
{
    public NodeType NodeType { get; }
    public Func<NodeExecutionContext, Task<NodeExecutionResult>> AsyncBehavior { get; set; } = _ => Task.FromResult(NodeExecutionResult.Ok("ok"));
    public Func<NodeExecutionContext, NodeExecutionResult> Behavior
    {
        set => AsyncBehavior = ctx => Task.FromResult(value(ctx));
    }
    public int InvocationCount { get; private set; }

    public FakeExecutor(NodeType type) { NodeType = type; }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        InvocationCount++;
        return await AsyncBehavior(ctx);
    }
}
