using FluentAssertions;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests;

internal sealed class EngineHarness : IDisposable
{
    public TestDb Db { get; }
    public IServiceProvider ServiceProvider { get; }
    public LoopEngine Engine { get; }
    public Dictionary<NodeType, FakeExecutor> Fakes { get; } = new();
    public Guid WorkItemId { get; private set; }
    public Guid RunId { get; private set; }
    public Dictionary<string, LoopNode> NodesById { get; } = new();
    public Dictionary<string, LoopNodeEdge> EdgesById { get; } = new();

    public EngineHarness()
    {
        Db = new TestDb();

        foreach (var type in Enum.GetValues<NodeType>())
            Fakes[type] = new FakeExecutor(type);

        var registry = new NodeExecutorRegistry(Fakes.Values);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INodeExecutorRegistry>(registry);
        services.AddSingleton<IRunNotifier, NoopRunNotifier>();
        services.AddSingleton<LoopEngine>();
        services.AddSingleton<IWorkItemStore>(Db.WorkItems);
        services.AddSingleton<ILoopRunStore>(Db.LoopRuns);
        services.AddSingleton<ILoopTemplateStore>(Db.LoopTemplates);
        services.AddSingleton<IProviderStore>(Db.Providers);
        services.AddSingleton<IEventLogStore>(Db.EventLogs);
        services.AddSingleton<IEventLogService>(new EventLogService(Db.EventLogs, Db.LoopRuns));
        services.AddSingleton<IAuthStore>(Db.Auth);
        services.AddSingleton<IRepositoryManager>(new Mock<IRepositoryManager>().Object);
        services.AddSingleton<IWorkItemManager>(sp => new WorkItemManager(
            sp.GetRequiredService<IWorkItemStore>(),
            sp.GetRequiredService<IRepositoryManager>(),
            sp.GetRequiredService<IEventLogService>(),
            sp.GetRequiredService<ILoopRunStore>()));

        ServiceProvider = services.BuildServiceProvider();
        Engine = ServiceProvider.GetRequiredService<LoopEngine>();
    }

    public void BuildSimpleGraph(params (string id, NodeType type)[] nodes)
    {
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
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
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running };
        Db.Context.WorkItems.Add(wi);
        Db.Context.LoopRuns.Add(run);
        Db.Context.SaveChanges();
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

    public IReadOnlyList<EventLog> ReloadEventLogs()
        => Db.Fresh().EventLogs.AsNoTracking().Where(e => e.LoopRunId == RunId).OrderBy(e => e.Sequence).ToList();

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

    public string DescribeInput(NodeExecutionContext ctx)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            nodeType = NodeType.ToString(),
            config = ctx.Node.Config,
        });

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        InvocationCount++;
        var result = await AsyncBehavior(ctx);
        if (NodeType == NodeType.Human)
            return new NodeOutcome.Suspended("Human node awaiting input", SuspendKind.HumanInput);
        if (NodeType == NodeType.PR && result.Success)
            return new NodeOutcome.Suspended("PR awaiting merge signal", SuspendKind.ExternalSignal, result.Output);
        if (NodeType == NodeType.Cleanup && result.Success)
            return new NodeOutcome.Terminal(result.Output);
        return NodeOutcome.FromResult(result);
    }
}
