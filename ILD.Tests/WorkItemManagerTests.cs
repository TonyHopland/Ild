using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

public class WorkItemManagerTests
{
    private static Guid SeedLoopRun(TestDb db, string workItemId)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var runId = Guid.NewGuid();
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = runId,
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.Running,
        });
        db.Context.SaveChanges();
        return runId;
    }

    private static (Guid runId, Guid versionId) SeedRunWithVersion(TestDb db, string workItemId)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var runId = Guid.NewGuid();
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = runId,
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
        });
        db.Context.SaveChanges();
        return (runId, version.Id);
    }

    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IRepositoryManager> repoMgr, Mock<IEventLogService> eventLog) Setup()
        => SetupCore(out _);

    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IRepositoryManager> repoMgr, Mock<IEventLogService> eventLog) SetupWithEngine(out Mock<ILoopEngine> engine)
        => SetupCore(out engine);

    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IRepositoryManager> repoMgr, Mock<IEventLogService> eventLog) SetupCore(out Mock<ILoopEngine> engine)
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();
        var repoMgr = new Mock<IRepositoryManager>();
        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(1L);
        engine = new Mock<ILoopEngine>();
        return (new WorkItemManager(repoMgr.Object, db.Providers, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions,
            engine: engine.Object), db, repo.Id, repoMgr, eventLog);
    }

    [Fact]
    public async Task CreateWorkItem_lands_in_WorkQueue_when_repo_default_is_WorkQueue()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var repo = await db.Context.Repositories.FindAsync(repoId);
        repo!.DefaultIntakeStatus = WorkItemStatus.WorkQueue;
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("title", "desc", repoId);

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.WorkQueue, wi!.Status);
    }

    [Fact]
    public async Task CreateWorkItem_lands_in_Backlog_when_repo_default_is_Backlog()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var repo = await db.Context.Repositories.FindAsync(repoId);
        repo!.DefaultIntakeStatus = WorkItemStatus.Backlog;
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("title", "desc", repoId);

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Backlog, wi!.Status);
    }

    [Fact]
    public async Task UpdateAsync_persists_title_and_description_and_touches_UpdatedAt()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("orig", "origdesc", repoId);
        var before = (await mgr.GetWorkItemAsync(id))!.UpdatedAt;
        await Task.Delay(5);

        var ok = await mgr.UpdateAsync(id, "new title", "new desc");
        Assert.True(ok);

        var reloaded = await mgr.GetWorkItemAsync(id);
        Assert.Equal("new title", reloaded!.Title);
        Assert.Equal("new desc", reloaded.Description);
        Assert.NotEqual(before, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_returns_false_for_unknown_id()
    {
        var (mgr, db, _, _, _) = Setup();
        using var _ = db;

        Assert.False((await mgr.UpdateAsync(Guid.NewGuid().ToString(), "t", "d")));
    }

    [Fact]
    public async Task IsReady_true_for_workitem_with_no_dependencies()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        await mgr.TransitionToWorkQueueAsync(id);

        Assert.True((await mgr.IsReadyAsync(id)));
    }

    [Fact]
    public async Task IsReady_false_when_dependency_not_merged()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);

        Assert.False((await mgr.IsReadyAsync(child)));
    }

    [Fact]
    public async Task IsReady_true_after_dependency_marked_done()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);

        var depRunId = SeedLoopRun(db, dep);
        var depRun = await db.Context.LoopRuns.FindAsync(depRunId);
        depRun!.Status = LoopRunStatus.Failed;
        await db.Context.SaveChangesAsync();

        await mgr.LinkPullRequestAsync(dep, "https://forgejo/pr/1");
        await mgr.ManuallyMarkMergedAsync(dep);

        Assert.True((await mgr.IsReadyAsync(child)));
    }

    [Fact]
    public async Task IsReady_false_when_dependency_merged_but_not_done()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);

        await mgr.LinkPullRequestAsync(dep, "https://forgejo/pr/1");
        await mgr.TransitionToHumanFeedbackAsync(dep, "Node Failed");

        Assert.False((await mgr.IsReadyAsync(child)));
    }

    [Fact]
    public async Task AddDependency_rejects_self_loop()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var act = async () => await mgr.AddDependencyAsync(id, id);
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task AddDependency_rejects_cycle()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var a = await mgr.CreateWorkItemAsync("a", "", repoId);
        var b = await mgr.CreateWorkItemAsync("b", "", repoId);
        var c = await mgr.CreateWorkItemAsync("c", "", repoId);

        await mgr.AddDependencyAsync(b, a);
        await mgr.AddDependencyAsync(c, b);

        var act = async () => await mgr.AddDependencyAsync(a, c);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransitionToReady_fails_when_dependencies_unmerged()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);
        await mgr.TransitionToWorkQueueAsync(child);

        var transitioned = await mgr.TransitionToReadyAsync(child);
        Assert.False(transitioned);
        var wi = await mgr.GetWorkItemAsync(child);
        Assert.Equal(RemoteWorkItemStatus.WorkQueue, wi!.Status);
    }

    [Fact]
    public async Task LinkPullRequest_persists_pr_url()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        SeedLoopRun(db, id);

        var result = await mgr.LinkPullRequestAsync(id, "https://forgejo/pr/42");

        Assert.True(result);
        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal("https://forgejo/pr/42", wi!.PrUrl);
    }

    [Fact]
    public async Task GetWorkItemAsync_leaves_StartedAt_and_CompletedAt_null_when_no_run()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Null(wi!.StartedAt);
        Assert.Null(wi.CompletedAt);
    }

    [Fact]
    public async Task GetWorkItemAsync_leaves_CurrentNodeLabel_null_when_no_run()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Null(wi!.CurrentNodeLabel);
    }

    [Fact]
    public async Task GetWorkItemAsync_surfaces_CurrentNodeLabel_from_running_run_current_node()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var (runId, versionId) = SeedRunWithVersion(db, id);
        var nodeId = Guid.NewGuid();
        db.Context.LoopNodes.Add(new LoopNode
        {
            Id = nodeId,
            LoopTemplateVersionId = versionId,
            NodeType = NodeType.AI,
            Label = "template-label",
        });
        db.Context.LoopRunNodes.Add(new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            NodeLabel = "implement-change",
            Status = LoopRunNodeStatus.Running,
        });
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.CurrentNodeId = nodeId;
        await db.Context.SaveChangesAsync();

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal("implement-change", wi!.CurrentNodeLabel);
    }

    [Fact]
    public async Task GetWorkItemAsync_CurrentNodeLabel_falls_back_to_template_node_label()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var (runId, versionId) = SeedRunWithVersion(db, id);
        var nodeId = Guid.NewGuid();
        db.Context.LoopNodes.Add(new LoopNode
        {
            Id = nodeId,
            LoopTemplateVersionId = versionId,
            NodeType = NodeType.AI,
            Label = "review",
        });
        // No NodeLabel on the run node — resolution must fall back to the
        // template node's label, mirroring how run nodes surface elsewhere.
        db.Context.LoopRunNodes.Add(new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            NodeLabel = null,
            Status = LoopRunNodeStatus.Running,
        });
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.CurrentNodeId = nodeId;
        await db.Context.SaveChangesAsync();

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal("review", wi!.CurrentNodeLabel);
    }

    [Fact]
    public async Task GetWorkItemAsync_surfaces_StartedAt_from_running_run_with_no_CompletedAt()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = SeedLoopRun(db, id);
        var started = DateTime.UtcNow;
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.Status = LoopRunStatus.Running;
        run.StartedAt = started;
        await db.Context.SaveChangesAsync();

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal(started, wi!.StartedAt);
        Assert.Null(wi.CompletedAt);
    }

    [Fact]
    public async Task GetWorkItemAsync_surfaces_StartedAt_and_CompletedAt_from_completed_run()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = SeedLoopRun(db, id);
        var started = DateTime.UtcNow.AddMinutes(-5);
        var completed = DateTime.UtcNow;
        // A successfully finished run is Completed, which the current-run
        // selection deliberately excludes — its timestamps must still surface.
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.Status = LoopRunStatus.Completed;
        run.StartedAt = started;
        run.CompletedAt = completed;
        await db.Context.SaveChangesAsync();

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal(started, wi!.StartedAt);
        Assert.Equal(completed, wi.CompletedAt);
    }

    [Fact]
    public async Task ManuallyMarkMerged_transitions_workitem_to_Done()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = SeedLoopRun(db, id);
        // Set the run to Failed so ManuallyMarkMerged transitions to Done
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.Status = LoopRunStatus.Failed;
        await db.Context.SaveChangesAsync();

        await mgr.LinkPullRequestAsync(id, "https://forgejo/pr/2");
        await mgr.ManuallyMarkMergedAsync(id);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Done, after!.Status);
        Assert.True(after.IsPrMerged);
    }

    [Fact]
    public async Task TransitionToHumanFeedback_sets_reason_on_workitem()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        await mgr.TransitionToReadyAsync(id);
        await mgr.TransitionToRunningAsync(id);
        SeedLoopRun(db, id);

        var ok = await mgr.TransitionToHumanFeedbackAsync(id, "PR Awaiting Merge");

        Assert.True(ok);
        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, wi!.Status);
        Assert.Equal("PR Awaiting Merge", wi.HumanFeedbackReason);
    }

    [Fact]
    public async Task ManuallyMarkMerged_does_not_set_Done_when_running_LoopRun_exists()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        await mgr.TransitionToRunningAsync(id);

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        await mgr.ManuallyMarkMergedAsync(id);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.True(after!.IsPrMerged);
        Assert.NotEqual(RemoteWorkItemStatus.Done, after.Status);
    }

    [Fact]
    public async Task ManuallyMarkMerged_sets_Done_when_LoopRun_is_Failed()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        await mgr.TransitionToReadyAsync(id);
        await mgr.TransitionToRunningAsync(id);

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Failed,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        await mgr.ManuallyMarkMergedAsync(id);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.True(after!.IsPrMerged);
        Assert.Equal(RemoteWorkItemStatus.Done, after.Status);
    }

    [Fact]
    public async Task CleanupToDone_keeps_worktree_and_marks_workitem_Done()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        await mgr.TransitionToHumanFeedbackAsync(id, "Node Failed");

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Failed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            WorktreePath = "/tmp/worktrees/test-wi",
            BranchName = "ild/wi-x-run-1",
            HumanFeedbackReason = "Node Failed",
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        await mgr.CleanupToDoneAsync(id);

        // Worktree and branch live exactly as long as the run row: nothing is
        // destroyed here — the retention sweeper (or a manual run delete)
        // reclaims them together with the run.
        repoMgr.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never);
        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Done, after!.Status);
        Assert.True(string.IsNullOrEmpty(after.HumanFeedbackReason));
        var freshRun = db.Fresh().LoopRuns.Single(r => r.Id == run.Id);
        Assert.Equal("/tmp/worktrees/test-wi", freshRun.WorktreePath);
        Assert.Equal("ild/wi-x-run-1", freshRun.BranchName);
        Assert.NotNull(freshRun.CompletedAt);
    }

    [Fact]
    public async Task CleanupToBacklog_keeps_worktree_resets_to_Backlog_and_finishes_run()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var runId = Guid.NewGuid();
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Cancelled,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);

        await mgr.TransitionToHumanFeedbackAsync(id, "Node Failed");
        run.WorktreePath = "/tmp/worktrees/test-wi";
        run.BranchName = "ild/wi-x-run-1";
        run.HumanFeedbackReason = "Node Failed";
        await db.Context.SaveChangesAsync();

        await mgr.CleanupToBacklogAsync(id);

        // Worktree and branch stay with the run row for inspection; the next
        // run gets its own branch/worktree, so nothing can leak into it.
        repoMgr.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never);
        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Backlog, after!.Status);
        Assert.True(string.IsNullOrEmpty(after.HumanFeedbackReason));
        // The Completed run is no longer the work item's current run.
        Assert.Null(after.CurrentLoopRunId);
        var freshRun = db.Fresh().LoopRuns.Single(r => r.Id == runId);
        Assert.Equal(LoopRunStatus.Completed, freshRun.Status);
        Assert.Equal("/tmp/worktrees/test-wi", freshRun.WorktreePath);
        Assert.Equal("ild/wi-x-run-1", freshRun.BranchName);
        Assert.NotNull(freshRun.CompletedAt);
    }

    [Fact]
    public async Task CommitAndPushBranch_commits_uncommitted_changes_and_pushes_branch()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;

        var worktree = Path.Combine(Path.GetTempPath(), "ild-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var id = await mgr.CreateWorkItemAsync("Keep my work", "", repoId);
            var runId = SeedLoopRun(db, id);
            var run = await db.Context.LoopRuns.FindAsync(runId);
            run!.WorktreePath = worktree;
            run.BranchName = "ild/wi-x-run-1";
            run.RepositoryId = repoId;
            await db.Context.SaveChangesAsync();

            repoMgr.Setup(r => r.GetDiffAsync(worktree)).ReturnsAsync("diff --git a b");
            repoMgr.Setup(r => r.CommitAsync(worktree, "Keep my work")).ReturnsAsync(true);
            repoMgr.Setup(r => r.PushAsync(worktree, "ild/wi-x-run-1", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync((true, (string?)null));

            var result = await mgr.CommitAndPushBranchAsync(id);

            Assert.True(result.Success);
            Assert.Equal("ild/wi-x-run-1", result.Branch);
            Assert.Null(result.Error);
            repoMgr.Verify(r => r.CommitAsync(worktree, "Keep my work"), Times.Once);
            repoMgr.Verify(r => r.PushAsync(worktree, "ild/wi-x-run-1", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Once);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task CommitAndPushBranch_skips_commit_when_worktree_is_clean()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;

        var worktree = Path.Combine(Path.GetTempPath(), "ild-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var id = await mgr.CreateWorkItemAsync("a", "", repoId);
            var runId = SeedLoopRun(db, id);
            var run = await db.Context.LoopRuns.FindAsync(runId);
            run!.WorktreePath = worktree;
            run.BranchName = "ild/wi-x-run-1";
            run.RepositoryId = repoId;
            await db.Context.SaveChangesAsync();

            // No diff — nothing to commit, but the branch should still be pushed.
            repoMgr.Setup(r => r.GetDiffAsync(worktree)).ReturnsAsync(string.Empty);
            repoMgr.Setup(r => r.PushAsync(worktree, "ild/wi-x-run-1", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync((true, (string?)null));

            var result = await mgr.CommitAndPushBranchAsync(id);

            Assert.True(result.Success);
            repoMgr.Verify(r => r.CommitAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            repoMgr.Verify(r => r.PushAsync(worktree, "ild/wi-x-run-1", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Once);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task CommitAndPushBranch_returns_error_when_no_worktree()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        SeedLoopRun(db, id); // run has no WorktreePath

        var result = await mgr.CommitAndPushBranchAsync(id);

        Assert.False(result.Success);
        Assert.Null(result.Branch);
        Assert.NotNull(result.Error);
        repoMgr.Verify(r => r.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
    }

    [Fact]
    public async Task CommitAndPushBranch_returns_error_when_push_fails()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;

        var worktree = Path.Combine(Path.GetTempPath(), "ild-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var id = await mgr.CreateWorkItemAsync("a", "", repoId);
            var runId = SeedLoopRun(db, id);
            var run = await db.Context.LoopRuns.FindAsync(runId);
            run!.WorktreePath = worktree;
            run.BranchName = "ild/wi-x-run-1";
            run.RepositoryId = repoId;
            await db.Context.SaveChangesAsync();

            repoMgr.Setup(r => r.GetDiffAsync(worktree)).ReturnsAsync(string.Empty);
            repoMgr.Setup(r => r.PushAsync(worktree, "ild/wi-x-run-1", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync((false, "no upstream"));

            var result = await mgr.CommitAndPushBranchAsync(id);

            Assert.False(result.Success);
            Assert.Null(result.Branch);
            Assert.Contains("no upstream", result.Error);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task SubmitHumanFeedbackInput_signals_engine_with_success_and_logs_event()
    {
        var (mgr, db, repoId, _, eventLog) = SetupWithEngine(out var engine);
        using var _ = db;

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = Guid.NewGuid();
        var humanNodeId = Guid.NewGuid();
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = humanNodeId,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(new LoopNode
        {
            Id = humanNodeId,
            LoopTemplateVersionId = ltv.Id,
            NodeType = NodeType.Human,
            Label = "ask-human",
        });
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = humanNodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        };
        db.Context.LoopRunNodes.Add(runNode);

        await mgr.TransitionToHumanFeedbackAsync(id, HumanFeedbackReasons.HumanInputNeeded);
        run.CurrentNodeId = humanNodeId;
        await db.Context.SaveChangesAsync();

        await mgr.SubmitHumanFeedbackInputAsync(id, "ship it");

        eventLog.Verify(e => e.AppendAsync(runId, "HumanFeedbackReceived", "ship it", null), Times.Once);
        engine.Verify(eng => eng.SignalNodeResultAsync(runId, runNode.Id,
            It.Is<NodeSignal>(s => s.Type == ExternalActionResultType.Success && s.Output == "ship it")), Times.Once);
    }

    [Fact]
    public async Task SubmitHumanFeedbackInput_without_engine_does_not_throw()
    {
        var (mgr, db, repoId, _, eventLog) = Setup();
        using var _ = db;

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.WaitingHuman,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = nodeId,
        });
        db.Context.LoopNodes.Add(new LoopNode
        {
            Id = nodeId,
            LoopTemplateVersionId = ltv.Id,
            NodeType = NodeType.Human,
            Label = "h",
        });
        db.Context.LoopRunNodes.Add(new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        });
        await mgr.TransitionToHumanFeedbackAsync(id, HumanFeedbackReasons.HumanInputNeeded);
        await db.Context.SaveChangesAsync();

        // Should not throw even without engine wired up
        var result = await mgr.SubmitHumanFeedbackInputAsync(id, "proceed");
        Assert.True(result);
        eventLog.Verify(e => e.AppendAsync(runId, "HumanFeedbackReceived", "proceed", null), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_removes_workitem_with_no_dependencies()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var ok = await mgr.DeleteAsync(id);

        Assert.True(ok);
        Assert.Null((await mgr.GetWorkItemAsync(id)));
    }

    [Fact]
    public async Task DeleteAsync_removes_workitem_and_all_dependencies()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);

        var ok = await mgr.DeleteAsync(child);

        Assert.True(ok);
        Assert.Null((await mgr.GetWorkItemAsync(child)));
        // Dependencies live on the WorkItemServer; delete propagates there.
    }

    [Fact]
    public async Task DeleteAsync_removes_workitem_when_other_items_depend_on_it()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);

        var ok = await mgr.DeleteAsync(dep);

        Assert.True(ok);
        Assert.Null((await mgr.GetWorkItemAsync(dep)));
        // Dependencies live on the WorkItemServer; delete propagates there.
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_unknown_id()
    {
        var (mgr, db, _, _, _) = Setup();
        using var _ = db;

        Assert.False((await mgr.DeleteAsync(Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task RejectHumanFeedback_signals_engine_with_failure_and_logs_event()
    {
        var (mgr, db, repoId, _, eventLog) = SetupWithEngine(out var engine);
        using var _ = db;

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = nodeId,
        };
        db.Context.LoopRuns.Add(run);

        var loopNode = new LoopNode
        {
            Id = nodeId,
            LoopTemplateVersionId = ltv.Id,
            NodeType = NodeType.Human,
            Label = "human-review",
        };
        db.Context.LoopNodes.Add(loopNode);

        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        };
        db.Context.LoopRunNodes.Add(runNode);

        await mgr.TransitionToHumanFeedbackAsync(id, "Human Input Needed");
        await db.Context.SaveChangesAsync();

        await mgr.RejectHumanFeedbackAsync(id);

        eventLog.Verify(e => e.AppendAsync(runId, "HumanFeedbackReceived", "rejected by user", null), Times.Once);
        engine.Verify(eng => eng.SignalNodeResultAsync(runId, runNode.Id,
            It.Is<NodeSignal>(s => s.Type == ExternalActionResultType.Reject)), Times.Once);
    }

    [Fact]
    public async Task RejectHumanFeedback_with_input_includes_text_in_signal_output_and_event_log()
    {
        var (mgr, db, repoId, _, eventLog) = SetupWithEngine(out var engine);
        using var _ = db;

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = nodeId,
        };
        db.Context.LoopRuns.Add(run);

        db.Context.LoopNodes.Add(new LoopNode
        {
            Id = nodeId,
            LoopTemplateVersionId = ltv.Id,
            NodeType = NodeType.Human,
            Label = "human-review",
        });

        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        };
        db.Context.LoopRunNodes.Add(runNode);

        await mgr.TransitionToHumanFeedbackAsync(id, ILD.Data.Enums.HumanFeedbackReasons.HumanInputNeeded);
        await db.Context.SaveChangesAsync();

        await mgr.RejectHumanFeedbackAsync(id, "looks wrong, try again with smaller scope");

        eventLog.Verify(e => e.AppendAsync(
            runId, "HumanFeedbackReceived",
            "rejected by user: looks wrong, try again with smaller scope",
            null), Times.Once);
        engine.Verify(eng => eng.SignalNodeResultAsync(runId, runNode.Id,
            It.Is<NodeSignal>(s => s.Type == ExternalActionResultType.Reject && s.Output == "looks wrong, try again with smaller scope")), Times.Once);
    }

    [Fact]
    public async Task SubmitHumanFeedbackRespond_signals_engine_with_success_and_logs_event()
    {
        var (mgr, db, repoId, _, eventLog) = SetupWithEngine(out var engine);
        using var _ = db;

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = nodeId,
        };
        db.Context.LoopRuns.Add(run);

        db.Context.LoopNodes.Add(new LoopNode
        {
            Id = nodeId,
            LoopTemplateVersionId = ltv.Id,
            NodeType = NodeType.Human,
            Label = "human-review",
        });

        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        };
        db.Context.LoopRunNodes.Add(runNode);

        await mgr.TransitionToHumanFeedbackAsync(id, "Human Input Needed");
        await db.Context.SaveChangesAsync();

        await mgr.SubmitHumanFeedbackRespondAsync(id, "please revise the approach");

        eventLog.Verify(e => e.AppendAsync(runId, "HumanFeedbackReceived", "please revise the approach", null), Times.Once);
        engine.Verify(eng => eng.SignalNodeResultAsync(runId, runNode.Id,
            It.Is<NodeSignal>(s => s.EdgeName == "Respond" && s.Output == "please revise the approach")), Times.Once);
    }

    // ---- TransitionAsync (canonical generic transition) ------------------

    [Fact]
    public async Task TransitionAsync_returns_false_for_unknown_workitem()
    {
        var (mgr, db, _, _, _) = Setup();
        using var _ = db;

        Assert.False((await mgr.TransitionAsync(Guid.NewGuid().ToString(), RemoteWorkItemStatus.Done)));
    }

    [Fact]
    public async Task TransitionAsync_to_HumanFeedback_stores_reason_and_actions()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        SeedLoopRun(db, id);
        await mgr.TransitionAsync(id, RemoteWorkItemStatus.HumanFeedback, "Need approval", "[\"approve\",\"reject\"]");

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, wi!.Status);
        Assert.Equal("Need approval", wi.HumanFeedbackReason);
        Assert.Equal("[\"approve\",\"reject\"]", wi.HumanFeedbackActions);
    }

    [Fact]
    public async Task TransitionAsync_to_non_HumanFeedback_clears_feedback_fields()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        await mgr.TransitionAsync(id, RemoteWorkItemStatus.HumanFeedback, "reason", "actions");
        await mgr.TransitionAsync(id, RemoteWorkItemStatus.Running);

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Running, wi!.Status);
        Assert.Null(wi.HumanFeedbackReason);
        Assert.Null(wi.HumanFeedbackActions);
    }

    [Fact]
    public async Task TransitionAsync_currentLoopRunId_null_leaves_existing_value()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        var existingRunId = SeedLoopRun(db, id);

        await mgr.TransitionAsync(id, RemoteWorkItemStatus.Running, currentLoopRunId: null);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.NotNull(after!.CurrentLoopRunId);
    }

    [Fact]
    public async Task TransitionAsync_currentLoopRunId_GuidEmpty_clears_value()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        var runId = SeedLoopRun(db, id);

        await mgr.TransitionAsync(id, RemoteWorkItemStatus.Done, currentLoopRunId: Guid.Empty);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Done, after!.Status);
    }

    [Fact]
    public async Task TransitionAsync_currentLoopRunId_value_sets_field()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        var newRunId = SeedLoopRun(db, id);

        await mgr.TransitionAsync(id, RemoteWorkItemStatus.Running, currentLoopRunId: newRunId);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(newRunId, after!.CurrentLoopRunId);
    }

    [Fact]
    public async Task TransitionAsync_notifies_state_change_only_when_status_actually_changes()
    {
        var db = new TestDb();
        using var _ = db;
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();
        var repoMgr = new Mock<IRepositoryManager>();
        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string>()))
            .ReturnsAsync(1L);
        var notifier = new Mock<IWorkItemNotifier>();
        var mgr = new WorkItemManager(repoMgr.Object, db.Providers, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions, notifier.Object);

        var id = await mgr.CreateWorkItemAsync("t", "", repo.Id);
        notifier.Invocations.Clear();

        // Same-status transition: no state-change notification, but HumanFeedback
        // notification still fires when transitioning to HumanFeedback with a reason.
        await mgr.TransitionAsync(id, RemoteWorkItemStatus.HumanFeedback, "first");
        await mgr.TransitionAsync(id, RemoteWorkItemStatus.HumanFeedback, "second");

        notifier.Verify(n => n.WorkItemStateChangedAsync(id, It.IsAny<RemoteWorkItemStatus>(), RemoteWorkItemStatus.HumanFeedback), Times.Once);
        notifier.Verify(n => n.HumanFeedbackRequiredAsync(id, "first"), Times.Once);
        notifier.Verify(n => n.HumanFeedbackRequiredAsync(id, "second"), Times.Once);
    }

    [Fact]
    public async Task CreateWorkItemAsync_notifies_state_change_so_clients_add_the_item_live()
    {
        var db = new TestDb();
        using var _ = db;
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git", DefaultIntakeStatus = WorkItemStatus.WorkQueue };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();
        var repoMgr = new Mock<IRepositoryManager>();
        var eventLog = new Mock<IEventLogService>();
        var notifier = new Mock<IWorkItemNotifier>();
        var mgr = new WorkItemManager(repoMgr.Object, db.Providers, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions, notifier.Object);

        var id = await mgr.CreateWorkItemAsync("t", "", repo.Id);

        // Creation has no prior status, so old and new are both the landing
        // status (here WorkQueue). The Taskboard's WorkItemStateChanged handler
        // loads any item it doesn't yet know about, so this single broadcast is
        // what makes a freshly created item appear without a manual refresh.
        notifier.Verify(n => n.WorkItemStateChangedAsync(id, RemoteWorkItemStatus.WorkQueue, RemoteWorkItemStatus.WorkQueue), Times.Once);
    }

    [Fact]
    public async Task CreateWorkItemAsync_notifies_for_agent_created_backlog_items()
    {
        var db = new TestDb();
        using var _ = db;
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git", DefaultIntakeStatus = WorkItemStatus.WorkQueue };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();
        var repoMgr = new Mock<IRepositoryManager>();
        var eventLog = new Mock<IEventLogService>();
        var notifier = new Mock<IWorkItemNotifier>();
        var mgr = new WorkItemManager(repoMgr.Object, db.Providers, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions, notifier.Object);

        // The AI/MCP path forces Backlog regardless of the repo's intake default.
        // This is the path the bug report was about — an agent-created item must
        // still broadcast so the board updates without a refresh.
        var runId = Guid.NewGuid();
        var id = await mgr.CreateWorkItemAsync("t", "", repo.Id, runId, forceBacklog: true);

        notifier.Verify(n => n.WorkItemStateChangedAsync(id, RemoteWorkItemStatus.Backlog, RemoteWorkItemStatus.Backlog), Times.Once);
    }
}
