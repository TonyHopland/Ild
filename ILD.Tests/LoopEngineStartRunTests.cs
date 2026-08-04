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

public class LoopEngineStartRunTests
{
    [Fact]
    public async Task StartRun_pins_the_templates_recovery_policy_on_the_run()
    {
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.NeedsReview, seedVersionAndStartNode: true);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Equal(RecoveryPolicy.NeedsReview, run.RecoveryPolicy);
    }

    [Fact]
    public async Task StartRun_parks_workitem_instead_of_throwing_when_template_has_no_version()
    {
        // An exception here would leave the work item claimed as Running on
        // the server with no run driving it — stuck, holding a slot forever.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, workItems) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: false);

        await engine.StartRunAsync(workItemId);

        Assert.Empty(db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId));
        workItems.Verify(w => w.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Theory]
    [InlineData(LoopRunStatus.Running)]
    [InlineData(LoopRunStatus.WaitingHuman)]
    public async Task StartRun_does_not_start_a_second_run_when_one_is_already_active(LoopRunStatus existingStatus)
    {
        // At-most-one-active-run invariant: a work item must never have two
        // runs alive at once. WaitingHuman covers the parked-run race (a
        // resumed run re-claimed by the stale-heartbeat reclaimer).
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true);
        var existing = SeedRun(db, workItemId, existingStatus);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var run = Assert.Single(db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId));
        Assert.Equal(existing.Id, run.Id);
    }

    [Fact]
    public async Task StartRun_starts_a_new_run_when_the_prior_run_is_terminal()
    {
        // The guard only blocks on alive runs — a completed prior run must not
        // prevent the next run on the same work item.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true);
        var completed = SeedRun(db, workItemId, LoopRunStatus.Completed);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var runs = db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Id != completed.Id);
    }

    [Fact]
    public async Task StartRun_leaves_the_branch_to_the_start_node_when_there_is_no_override()
    {
        // The overwhelming majority of work items: a null BranchName on the run
        // is what makes StartNodeExecutor generate ild/wi-<id>-run-<run>.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            branchNameOverride: "   ");

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Null(run.BranchName);
    }

    [Fact]
    public async Task StartRun_pins_the_override_on_the_run_verbatim()
    {
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            branchNameOverride: "feature/foo");

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Equal("feature/foo", run.BranchName);
    }

    [Theory]
    [InlineData("Branch `feature/foo` is already used locally by run 12 of this work item.")]
    [InlineData("Branch `feature/foo` already exists on origin.")]
    public async Task StartRun_parks_without_creating_a_run_when_the_override_is_taken(string conflict)
    {
        // Parking before the run exists is the whole point: no LoopRun row, no
        // worktree, no concurrency slot held while the human sorts it out.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, workItems) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            branchNameOverride: "feature/foo",
            verdict: new BranchNameVerdict(null, conflict));

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        Assert.Empty(db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId));
        workItems.Verify(w => w.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback,
            conflict, It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task StartRun_parks_without_creating_a_run_when_the_override_is_illegal()
    {
        // A name that never went through the API's validation (an older row, a
        // direct write to the work-item server) must not reach git.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var (engine, workItems) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            branchNameOverride: "feature foo",
            verdict: new BranchNameVerdict("Branch name cannot contain spaces.", null));

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        Assert.Empty(db.Fresh().LoopRuns.Where(r => r.WorkItemId == workItemId));
        workItems.Verify(w => w.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback,
            "Branch name cannot contain spaces.", It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Editing_the_override_after_a_run_started_does_not_rename_that_runs_branch()
    {
        // The override is pinned on the run at creation, so a later edit only
        // reaches the item's next run — nothing renames a live branch.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var wi = new WorkItemView { Id = workItemId, Tags = new[] { "tag" }, BranchNameOverride = "feature/foo" };
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            workItemView: wi);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        wi.BranchNameOverride = "feature/renamed";

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Equal("feature/foo", run.BranchName);
    }

    [Fact]
    public async Task StartRun_pins_the_base_branch_override_on_the_run()
    {
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var wi = new WorkItemView { Id = workItemId, Tags = new[] { "tag" }, BaseBranchOverride = " release/1.0 " };
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            workItemView: wi);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Equal("release/1.0", run.BaseBranchOverride);
    }

    [Fact]
    public async Task StartRun_leaves_the_base_branch_null_when_the_item_has_no_override()
    {
        // Null means "the repository's default branch" — the base every run had
        // before overrides existed — so nothing needs backfilling.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var wi = new WorkItemView { Id = workItemId, Tags = new[] { "tag" }, BaseBranchOverride = "   " };
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            workItemView: wi);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Null(run.BaseBranchOverride);
    }

    [Fact]
    public async Task Editing_the_base_branch_after_a_run_started_does_not_move_that_runs_base()
    {
        // The Start node builds from this ref and the PR node targets it, hours
        // apart. An edit in between must not open the PR against a branch the
        // worktree was never rebased onto.
        using var db = new TestDb();
        var workItemId = $"WI-{Guid.NewGuid():N}";
        var wi = new WorkItemView { Id = workItemId, Tags = new[] { "tag" }, BaseBranchOverride = "release/1.0" };
        var (engine, _) = BuildEngine(db, workItemId, RecoveryPolicy.AutoResume, seedVersionAndStartNode: true,
            workItemView: wi);

        await engine.StartRunAsync(workItemId);
        await DrainAsync(engine);

        wi.BaseBranchOverride = "release/2.0";

        var run = db.Fresh().LoopRuns.Single(r => r.WorkItemId == workItemId);
        Assert.Equal("release/1.0", run.BaseBranchOverride);
    }

    private static LoopRun SeedRun(TestDb db, string workItemId, LoopRunStatus status)
    {
        var versionId = db.Context.LoopTemplateVersions.Select(v => v.Id).First();
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = versionId,
            Status = status,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    private static (ILoopEngine engine, Mock<IWorkItemManager> workItems) BuildEngine(
        TestDb db, string workItemId, RecoveryPolicy templatePolicy, bool seedVersionAndStartNode,
        string? branchNameOverride = null,
        BranchNameVerdict? verdict = null,
        WorkItemView? workItemView = null)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = templatePolicy };
        db.Context.LoopTemplates.Add(template);
        if (seedVersionAndStartNode)
        {
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
            db.Context.LoopTemplateVersions.Add(version);
            db.Context.LoopNodes.Add(new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = version.Id,
                NodeType = NodeType.Start,
                Label = "start",
            });
        }
        db.Context.SaveChanges();

        var workItems = new Mock<IWorkItemManager>();
        var wi = workItemView ?? new WorkItemView
        {
            Id = workItemId,
            Tags = new[] { "tag" },
            BranchNameOverride = branchNameOverride,
        };
        workItems.Setup(w => w.GetWorkItemAsync(workItemId)).ReturnsAsync(wi);
        workItems.Setup(w => w.TransitionAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
            .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, template.Id, new[] { "t" }));

        // The Start node executor just terminates so the background loop exits
        // quickly and deterministically.
        var registry = new ScriptedExecutorRegistry();
        registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"), new NodeOutcome.Terminal("done")));

        var services = new ServiceCollection();
        services.AddSingleton(db.Context);
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton<ILoopTemplateStore>(db.LoopTemplates);
        services.AddSingleton<IWorkItemManager>(workItems.Object);
        services.AddSingleton(resolver.Object);
        services.AddSingleton<IRunNotifier, NoopRunNotifier>();
        services.AddSingleton<INodeExecutorRegistry>(registry);

        var branchNames = new Mock<IBranchNameOverrideService>();
        branchNames.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(verdict ?? BranchNameVerdict.Usable);
        services.AddSingleton(branchNames.Object);
        var sp = services.BuildServiceProvider();

        var engine = new LoopEngine(sp, registry, sp.GetRequiredService<IRunNotifier>(), NullLogger<LoopEngine>.Instance);
        return (engine, workItems);
    }

    /// <summary>Waits for the engine's background run loop to park.</summary>
    private static async Task DrainAsync(ILoopEngine engine)
    {
        for (var i = 0; i < 200 && (await engine.GetActiveRunIdsAsync()).Any(); i++)
            await Task.Delay(10);
    }
}
