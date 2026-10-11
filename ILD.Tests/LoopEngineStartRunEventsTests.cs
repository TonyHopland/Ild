using System.Reflection;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Starting a run: the start is on the new run's timeline, a start that cannot
/// make a run says why on the work item rather than on any earlier run, and a
/// work item never ends up with two live runs or an orphaned one.
/// </summary>
public class LoopEngineStartRunEventsTests
{
    private sealed class AlwaysTerminal(NodeType type) : INodeExecutor
    {
        public NodeType NodeType => type;

        public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
        {
            await Task.Yield();
            yield return new NodeOutcome.NodeStarting("start");
            yield return new NodeOutcome.Terminal("done");
        }
    }

    /// <summary>Forwards to a loose mock, letting a test take over chosen calls by name.</summary>
    public class Intercepting<T> : DispatchProxy where T : class
    {
        private T _inner = null!;
        private Func<MethodInfo, object?[], (bool Handled, object? Result)> _hook = null!;

        public static T Wrap(T inner, Func<MethodInfo, object?[], (bool Handled, object? Result)> hook)
        {
            var proxy = Create<T, Intercepting<T>>();
            var self = (Intercepting<T>)(object)proxy;
            self._inner = inner;
            self._hook = hook;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var (handled, result) = _hook(targetMethod!, args ?? Array.Empty<object?>());
            return handled ? result : targetMethod!.Invoke(_inner, args);
        }
    }

    private sealed class Rig : IDisposable
    {
        public TestDb Db { get; } = new();
        public IEventLogService Events { get; }
        public Mock<ILoopTemplateResolver> Resolver { get; } = new();
        public Mock<IBranchNameOverrideService> Branches { get; } = new();
        public LoopEngine Engine { get; }
        public Guid RepoId { get; }
        private IWorkItemManager _workItems = null!;

        public Rig()
        {
            Events = RunTimeline.EventLog(Db);
            var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example.test" };
            var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example.test/repo.git" };
            Db.Context.RemoteProviders.Add(remote);
            Db.Context.Repositories.Add(repo);
            Db.Context.SaveChanges();
            RepoId = repo.Id;
            Branches.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BranchNameVerdict.Usable);

            var registry = new ScriptedExecutorRegistry();
            registry.Register(new AlwaysTerminal(NodeType.Start));
            var services = new ServiceCollection();
            services.AddSingleton(Db.Context);
            services.AddSingleton(Db.LoopRuns);
            services.AddSingleton(Db.LoopTemplates);
            services.AddSingleton(Db.EventLogs);
            services.AddSingleton(Events);
            services.AddSingleton(Db.Settings);
            services.AddSingleton<ISchedulerSettingsService>(new SchedulerSettingsService(Db.Settings));
            services.AddSingleton(_ => _workItems);
            services.AddSingleton(Resolver.Object);
            services.AddSingleton(Branches.Object);
            services.AddSingleton<IRunNotifier, NoopRunNotifier>();
            services.AddSingleton<INodeExecutorRegistry>(registry);
            var sp = services.BuildServiceProvider();
            Engine = new LoopEngine(sp, registry, sp.GetRequiredService<IRunNotifier>(), NullLogger<LoopEngine>.Instance);
        }

        public WorkItemManager UseRealManager()
        {
            var manager = RunTimeline.Manager(Db, eventLog: Events);
            _workItems = manager;
            return manager;
        }

        public void UseManager(IWorkItemManager workItems) => _workItems = workItems;

        public Guid Template(bool withVersion = true, bool withStart = true)
        {
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t-" + Guid.NewGuid().ToString("N")[..6] };
            Db.Context.LoopTemplates.Add(template);
            if (withVersion)
            {
                var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
                Db.Context.LoopTemplateVersions.Add(version);
                if (withStart)
                    Db.Context.LoopNodes.Add(new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Start, Label = "start" });
            }
            Db.Context.SaveChanges();
            return template.Id;
        }

        public void Resolves(LoopTemplateResolutionKind kind, Guid? templateId, params string[] names)
            => Resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(kind, templateId, names));

        public async Task DrainAsync()
        {
            foreach (var runId in await Engine.GetActiveRunIdsAsync())
                await LoopEngineHarness.WaitUntilIdleAsync(Engine, runId);
        }

        public IReadOnlyList<LoopRun> Runs(string workItemId)
            => Db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId).ToList();

        public void Dispose() => Db.Dispose();
    }

    private static IWorkItemManager ManagerSeeing(string workItemId, Func<MethodInfo, object?[], (bool, object?)>? hook = null)
    {
        var loose = new Mock<IWorkItemManager>();
        loose.Setup(w => w.GetWorkItemAsync(workItemId))
            .ReturnsAsync(new WorkItemView { Id = workItemId, Tags = new[] { "tag" } });
        return Intercepting<IWorkItemManager>.Wrap(loose.Object, (method, args) =>
            method.Name == nameof(IWorkItemManager.TransitionAsync) && hook is null
                ? (true, Task.FromResult(true))
                : hook?.Invoke(method, args) ?? (false, null));
    }

    [Fact]
    public async Task A_started_run_opens_its_timeline_with_the_start()
    {
        using var rig = new Rig();
        var workItemId = "WI-" + Guid.NewGuid().ToString("N");
        rig.UseManager(ManagerSeeing(workItemId));
        rig.Resolves(LoopTemplateResolutionKind.Single, rig.Template(), "t");

        await rig.Engine.StartRunAsync(workItemId, TestContext.Current.CancellationToken);
        await rig.DrainAsync();

        var run = Assert.Single(rig.Runs(workItemId));
        var events = RunTimeline.Events(rig.Db, run.Id);
        Assert.Equal(EventType.LoopRunStarted, events[0].EventType);
        Assert.Single(events, e => e.EventType == EventType.LoopRunStarted);
    }

    [Fact]
    public async Task A_start_that_loses_the_race_to_another_start_leaves_the_winner_alone_and_does_not_throw()
    {
        using var rig = new Rig();
        var workItemId = "WI-" + Guid.NewGuid().ToString("N");
        rig.UseManager(ManagerSeeing(workItemId));
        var templateId = rig.Template();
        var versionId = rig.Db.Context.LoopTemplateVersions.Single(v => v.LoopTemplateId == templateId).Id;
        var winner = Guid.NewGuid();
        // The competing start commits between this start's "is one active?" check and its own insert.
        rig.Resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
            .Callback(() =>
            {
                using var other = rig.Db.Fresh();
                other.LoopRuns.Add(new LoopRun
                {
                    Id = winner,
                    WorkItemId = workItemId,
                    LoopTemplateVersionId = versionId,
                    Status = LoopRunStatus.Running,
                    StartedAt = DateTime.UtcNow,
                    RecoveryPolicy = RecoveryPolicy.AutoResume,
                });
                other.SaveChanges();
            })
            .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, templateId, new[] { "t" }));

        var thrown = await Record.ExceptionAsync(() => rig.Engine.StartRunAsync(workItemId, TestContext.Current.CancellationToken));
        await rig.DrainAsync();

        Assert.Null(thrown);
        var run = Assert.Single(rig.Runs(workItemId));
        Assert.Equal(winner, run.Id);
        Assert.Equal(LoopRunStatus.Running, run.Status);
    }

    [Fact]
    public async Task A_start_failing_after_its_run_exists_ends_that_run_failed_with_the_reason()
    {
        using var rig = new Rig();
        var workItemId = "WI-" + Guid.NewGuid().ToString("N");
        const string cause = "work item server unreachable: connection refused (example.test:8080)";
        rig.UseManager(ManagerSeeing(workItemId, (method, args) =>
            method.Name == nameof(IWorkItemManager.TransitionAsync)
                ? (true, args[1] is RemoteWorkItemStatus.Running
                    ? Task.FromException<bool>(new HttpRequestException(cause))
                    : Task.FromResult(true))
                : (false, null)));
        rig.Resolves(LoopTemplateResolutionKind.Single, rig.Template(), "t");

        await Record.ExceptionAsync(() => rig.Engine.StartRunAsync(workItemId, TestContext.Current.CancellationToken));
        await rig.DrainAsync();

        var run = Assert.Single(rig.Runs(workItemId));
        Assert.Equal(LoopRunStatus.Failed, run.Status);
        Assert.NotNull(run.CompletedAt);
        var failed = Assert.Single(RunTimeline.EndingEvents(rig.Db, run.Id));
        Assert.Equal(EventType.LoopRunFailed, failed.EventType);
        Assert.Contains(cause, failed.Data);
    }

    public enum PreRun { NoTemplate, AmbiguousTemplates, NoVersion, NoStartNode, BranchTaken }

    [Theory]
    [InlineData(PreRun.NoTemplate, "No loop found for existing tags")]
    [InlineData(PreRun.AmbiguousTemplates, "Multiple loop templates match tags: build, deploy")]
    [InlineData(PreRun.NoVersion, "Matched loop template has no version")]
    [InlineData(PreRun.NoStartNode, "Matched loop template has no Start node")]
    [InlineData(PreRun.BranchTaken, "Branch `feature/taken` already exists on origin.")]
    public async Task A_start_that_cannot_make_a_run_parks_the_item_with_its_reason_and_leaves_earlier_runs_alone(
        PreRun failure, string reason)
    {
        using var rig = new Rig();
        var manager = rig.UseRealManager();
        var workItemId = await manager.CreateWorkItemAsync("t", "d", rig.RepoId, null, false,
            tags: new[] { "build" }, branchNameOverride: failure == PreRun.BranchTaken ? "feature/taken" : null);

        var earlier = RunTimeline.SeedRun(rig.Db, workItemId, RunTimeline.SeedVersion(rig.Db),
            LoopRunStatus.Failed, humanFeedbackReason: "Tests failed twice");
        await rig.Events.AppendAsync(earlier.Id, EventType.LoopRunFailed, "Tests failed twice");

        switch (failure)
        {
            case PreRun.NoTemplate: rig.Resolves(LoopTemplateResolutionKind.None, null); break;
            case PreRun.AmbiguousTemplates: rig.Resolves(LoopTemplateResolutionKind.Ambiguous, null, "build", "deploy"); break;
            case PreRun.NoVersion: rig.Resolves(LoopTemplateResolutionKind.Single, rig.Template(withVersion: false), "t"); break;
            case PreRun.NoStartNode: rig.Resolves(LoopTemplateResolutionKind.Single, rig.Template(withStart: false), "t"); break;
            case PreRun.BranchTaken:
                rig.Resolves(LoopTemplateResolutionKind.Single, rig.Template(), "t");
                rig.Branches.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new BranchNameVerdict(null, reason));
                break;
        }
        var before = DateTime.UtcNow.AddSeconds(-5);

        await rig.Engine.StartRunAsync(workItemId, TestContext.Current.CancellationToken);
        await rig.DrainAsync();

        Assert.Equal(earlier.Id, Assert.Single(rig.Runs(workItemId)).Id);
        var untouched = rig.Db.Fresh().LoopRuns.Single(r => r.Id == earlier.Id);
        Assert.Equal("Tests failed twice", untouched.HumanFeedbackReason);
        Assert.Equal(LoopRunStatus.Failed, untouched.Status);
        Assert.Equal(new[] { "Tests failed twice" }, RunTimeline.Events(rig.Db, earlier.Id).Select(e => e.Data));

        var view = await manager.GetWorkItemAsync(workItemId);
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, view!.Status);
        Assert.Contains(reason, view.StatusReason);
        Assert.NotNull(view.StatusReasonAt);
        Assert.InRange(view.StatusReasonAt!.Value, before, DateTime.UtcNow.AddSeconds(5));
        var listed = (await manager.ListAsync(null, null, null, 0, 100)).Single(w => w.Id == workItemId);
        Assert.Equal(view.StatusReason, listed.StatusReason);
        Assert.Equal(view.StatusReasonAt, listed.StatusReasonAt);

        rig.Resolves(LoopTemplateResolutionKind.Single, rig.Template(), "t");
        rig.Branches.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchNameVerdict.Usable);

        await rig.Engine.StartRunAsync(workItemId, TestContext.Current.CancellationToken);
        await rig.DrainAsync();

        Assert.Equal(2, rig.Runs(workItemId).Count);
        var cleared = await manager.GetWorkItemAsync(workItemId);
        Assert.Null(cleared!.StatusReason);
        Assert.Null(cleared.StatusReasonAt);
    }
}
