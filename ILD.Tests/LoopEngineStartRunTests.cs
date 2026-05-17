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

public class LoopEngineStartRunTests
{
    [Fact]
    public async Task StartRunAsync_registers_manual_start_with_active_tracker()
    {
        await using var h = new ManualStartHarness();
        var workItemId = await h.CreateReadyWorkItemAsync();

        await h.Engine.StartRunAsync(workItemId);

        Assert.Contains(workItemId, h.Tracker.Snapshot());
        Assert.Equal(1, h.RunCount(workItemId));
    }

    [Fact]
    public async Task StartRunAsync_rejects_second_start_when_run_already_exists()
    {
        await using var h = new ManualStartHarness();
        var workItemId = await h.CreateReadyWorkItemAsync();
        h.SeedRunningRun(workItemId);

        var act = () => h.Engine.StartRunAsync(workItemId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains($"WorkItem {workItemId} already has a non-completed run", ex.Message);
        Assert.Contains(workItemId, h.Tracker.Snapshot());
        Assert.Equal(1, h.RunCount(workItemId));
    }

    private sealed class ManualStartHarness : IAsyncDisposable
    {
        private readonly TestDb _db = new();
        private readonly TaskCompletionSource<NodeExecutionResult> _cmdGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Repository _repository;
        private readonly LoopTemplate _template;
        private readonly LoopTemplateVersion _version;
        private readonly LoopNode _cmdNode;
        private readonly ServiceProvider _serviceProvider;
        private readonly IWorkItemManager _workItemManager;

        public ManualStartHarness()
        {
            var remote = new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "remote",
                Type = "Forgejo",
                Url = "https://example.test",
            };
            _repository = new Repository
            {
                Id = Guid.NewGuid(),
                Name = "repo",
                RemoteProviderId = remote.Id,
                CloneUrl = "https://example.test/repo.git",
                DefaultIntakeStatus = WorkItemStatus.Ready,
            };
            _template = new LoopTemplate
            {
                Id = Guid.NewGuid(),
                Name = "manual-start-template",
                Description = "d",
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                MaxNodeExecutions = 100,
                MaxWallClockHours = 24,
            };
            _version = new LoopTemplateVersion
            {
                Id = Guid.NewGuid(),
                LoopTemplateId = _template.Id,
                VersionNumber = 1,
                CreatedAt = DateTime.UtcNow,
            };

            var startNode = new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = _version.Id,
                NodeType = NodeType.Start,
                Label = "start",
            };
            _cmdNode = new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = _version.Id,
                NodeType = NodeType.Cmd,
                Label = "cmd",
            };
            var cleanupNode = new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = _version.Id,
                NodeType = NodeType.Cleanup,
                Label = "cleanup",
            };

            _db.Context.RemoteProviders.Add(remote);
            _db.Context.Repositories.Add(_repository);
            _db.Context.LoopTemplates.Add(_template);
            _db.Context.LoopTemplateVersions.Add(_version);
            _db.Context.LoopNodes.AddRange(startNode, _cmdNode, cleanupNode);
            _db.Context.LoopNodeEdges.AddRange(
                new LoopNodeEdge
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = startNode.Id,
                    TargetNodeId = _cmdNode.Id,
                    EdgeType = EdgeType.OnSuccess,
                },
                new LoopNodeEdge
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = _cmdNode.Id,
                    TargetNodeId = cleanupNode.Id,
                    EdgeType = EdgeType.OnSuccess,
                });
            _db.Context.SaveChanges();

            var startExecutor = new FakeExecutor(NodeType.Start);
            var cmdExecutor = new FakeExecutor(NodeType.Cmd)
            {
                AsyncBehavior = _ => _cmdGate.Task,
            };
            var cleanupExecutor = new FakeExecutor(NodeType.Cleanup);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<INodeExecutorRegistry>(new NodeExecutorRegistry(new[] { startExecutor, cmdExecutor, cleanupExecutor }));
            services.AddSingleton<IRunNotifier, NoopRunNotifier>();
            services.AddSingleton<IWorkItemNotifier, NoopWorkItemNotifier>();
            services.AddSingleton<IActiveWorkItemTracker, InMemoryActiveWorkItemTracker>();
            services.AddSingleton<LoopEngine>();
            services.AddSingleton<ILoopRunStore>(_db.LoopRuns);
            services.AddSingleton<ILoopTemplateStore>(_db.LoopTemplates);
            services.AddSingleton<IProviderStore>(_db.Providers);
            services.AddSingleton<IEventLogStore>(_db.EventLogs);
            services.AddSingleton<IEventLogService>(new EventLogService(_db.EventLogs, _db.LoopRuns));
            services.AddSingleton<IRepositoryManager>(new Mock<IRepositoryManager>().Object);
            services.AddSingleton<IWorkItemServerClient>(_db.Server.Client);
            services.AddSingleton<IWorkItemServerOptionsResolver>(_db.Server.Options);
            services.AddSingleton<ILoopTemplateResolver>(new FixedTemplateResolver(_template.Id, _template.Name));
            services.AddSingleton<IWorkItemManager>(sp => new WorkItemManager(
                sp.GetRequiredService<IRepositoryManager>(),
                sp.GetRequiredService<IProviderStore>(),
                sp.GetRequiredService<IEventLogService>(),
                sp.GetRequiredService<ILoopRunStore>(),
                sp.GetRequiredService<IWorkItemServerClient>(),
                sp.GetRequiredService<IWorkItemServerOptionsResolver>(),
                sp.GetRequiredService<IWorkItemNotifier>()));

            _serviceProvider = services.BuildServiceProvider();
            Engine = _serviceProvider.GetRequiredService<LoopEngine>();
            Tracker = (InMemoryActiveWorkItemTracker)_serviceProvider.GetRequiredService<IActiveWorkItemTracker>();
            _workItemManager = _serviceProvider.GetRequiredService<IWorkItemManager>();
        }

        public LoopEngine Engine { get; }

        public InMemoryActiveWorkItemTracker Tracker { get; }

        public async Task<string> CreateReadyWorkItemAsync()
        {
            return await _workItemManager.CreateWorkItemAsync(
                "Manual start test",
                "d",
                _repository.Id,
                createdByLoopRunId: null,
                forceBacklog: false,
                tags: new[] { _template.Name });
        }

        public int RunCount(string workItemId)
        {
            return _db.Context.LoopRuns.Count(r => r.WorkItemId == workItemId);
        }

        public void SeedRunningRun(string workItemId)
        {
            _db.Context.LoopRuns.Add(new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                LoopTemplateVersionId = _version.Id,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.Running,
                RepositoryId = _repository.Id,
                StartedAt = DateTime.UtcNow,
            });
            _db.Context.SaveChanges();
        }

        public async ValueTask DisposeAsync()
        {
            _cmdGate.TrySetResult(NodeExecutionResult.Ok("ok"));
            await Task.Delay(50);
            await _serviceProvider.DisposeAsync();
            _db.Dispose();
        }
    }

    private sealed class FixedTemplateResolver(Guid templateId, string tag) : ILoopTemplateResolver
    {
        public LoopTemplateResolution Resolve(IReadOnlyList<string> tags)
            => tags.Contains(tag, StringComparer.Ordinal)
                ? new LoopTemplateResolution(LoopTemplateResolutionKind.Single, templateId, Array.Empty<string>())
                : new LoopTemplateResolution(LoopTemplateResolutionKind.None, null, Array.Empty<string>());
    }
}