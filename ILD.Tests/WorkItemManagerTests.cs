using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Text.Json;

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

    /// <param name="server">
    /// A WorkItemServer harness to attach to instead of standing up a fresh
    /// one — for tests that outlive the ILD-local database.
    /// </param>
    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IRepositoryManager> repoMgr, Mock<IEventLogService> eventLog) Setup(
        FakeWorkItemServerHarness? server = null)
        => SetupCore(out _, server: server);

    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IRepositoryManager> repoMgr, Mock<IEventLogService> eventLog) SetupWithEngine(out Mock<ILoopEngine> engine)
        => SetupCore(out engine);

    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IRepositoryManager> repoMgr, Mock<IEventLogService> eventLog) SetupCore(out Mock<ILoopEngine> engine, Mock<IRemoteProvider>? remoteProvider = null, FakeWorkItemServerHarness? server = null)
    {
        var db = new TestDb(server);
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();
        var repoMgr = new Mock<IRepositoryManager>();
        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(1L);
        engine = new Mock<ILoopEngine>();
        return (new WorkItemManager(repoMgr.Object, db.Providers, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions,
            engine: engine.Object, remoteProvider: remoteProvider?.Object), db, repo.Id, repoMgr, eventLog);
    }

    // Park a work item at a PR node awaiting merge: a running run carrying the
    // PR URL + branch, plus a WaitingHuman run node so the OnSuccess
    // continuation has a node to signal. The CloneUrl is fixed by SetupCore.
    private const string MergeRepoCloneUrl = "https://example/repo.git";

    private static (string workItemId, Guid runId, Guid runNodeId) SeedPrAwaitingMerge(
        WorkItemManager mgr, TestDb db, Guid repoId, string prUrl, string branchName)
    {
        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "pr" };
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplates.Add(lt);
        db.Context.LoopTemplateVersions.Add(ltv);
        db.Context.SaveChanges();

        var id = mgr.CreateWorkItemAsync("merge me", "", repoId).GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        var prNodeId = Guid.NewGuid();
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = prNodeId,
            PrUrl = prUrl,
            BranchName = branchName,
        });
        db.Context.LoopNodes.Add(new LoopNode
        {
            Id = prNodeId,
            LoopTemplateVersionId = ltv.Id,
            NodeType = NodeType.PR,
            Label = "pr",
        });
        var runNodeId = Guid.NewGuid();
        db.Context.LoopRunNodes.Add(new LoopRunNode
        {
            Id = runNodeId,
            LoopRunId = runId,
            LoopNodeId = prNodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        });
        db.Context.SaveChanges();
        mgr.TransitionToHumanFeedbackAsync(id, HumanFeedbackReasons.PrAwaitingMerge).GetAwaiter().GetResult();
        return (id, runId, runNodeId);
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
    public async Task UpdateAsync_notifies_state_change_so_clients_refresh_the_card_live()
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

        var id = await mgr.CreateWorkItemAsync("orig", "origdesc", repo.Id);
        notifier.Invocations.Clear();

        var ok = await mgr.UpdateAsync(id, "new title", "new desc");
        Assert.True(ok);

        // An edit keeps the status, so old and new are both the current status.
        // The Taskboard's WorkItemStateChanged handler re-fetches the item, which
        // is what surfaces the new title/description without a page refresh. This
        // is the AI/MCP edit path the bug report was about.
        notifier.Verify(n => n.WorkItemStateChangedAsync(id, RemoteWorkItemStatus.WorkQueue, RemoteWorkItemStatus.WorkQueue), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_does_not_notify_for_unknown_id()
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
        var notifier = new Mock<IWorkItemNotifier>();
        var mgr = new WorkItemManager(repoMgr.Object, db.Providers, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions, notifier.Object);

        var ok = await mgr.UpdateAsync(Guid.NewGuid().ToString(), "t", "d");
        Assert.False(ok);

        // A no-op update (item gone) must not broadcast — a phantom refresh would
        // make clients re-fetch an item that does not exist.
        notifier.Verify(n => n.WorkItemStateChangedAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()), Times.Never);
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

        await mgr.CleanupToDoneAsync(dep);

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

    // ── PullBranchAsync ────────────────────────────────────────────────────
    //
    // The inverse of the push path: an orchestrator-credentialed fetch plus a
    // local rebase of the run branch onto its OWN remote counterpart, so an agent
    // (which under ADR-0014 has neither the token nor the askpass helper) can pick
    // up commits pushed to its branch after the run started.

    private const string PullBranchName = "ild/wi-x-run-1";
    private const string PullUpstream = "origin/" + PullBranchName;

    /// <summary>
    /// A work item whose current run owns a real (empty) worktree directory on
    /// <see cref="PullBranchName"/> — the state every pull path starts from. The
    /// directory is returned so the test can delete it.
    /// </summary>
    private static async Task<(string Id, string Worktree)> SeedWorktreeWorkItemAsync(
        WorkItemManager mgr, TestDb db, Guid repoId)
    {
        var worktree = Path.Combine(Path.GetTempPath(), "ild-pull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        var id = await mgr.CreateWorkItemAsync("Pull me", "", repoId);
        var runId = SeedLoopRun(db, id);
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.WorktreePath = worktree;
        run.BranchName = PullBranchName;
        run.RepositoryId = repoId;
        await db.Context.SaveChangesAsync();
        return (id, worktree);
    }

    /// <summary>Clean worktree, reachable origin, remote counterpart present.</summary>
    private static void SetupPullable(Mock<IRepositoryManager> repoMgr, string worktree, int behind)
    {
        repoMgr.Setup(r => r.GetUncommittedFilesAsync(worktree)).ReturnsAsync(Array.Empty<string>());
        repoMgr.Setup(r => r.FetchAsync(worktree, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);
        repoMgr.Setup(r => r.RemoteBranchExistsAsync(worktree, PullBranchName)).ReturnsAsync(true);
        repoMgr.Setup(r => r.GetCommitsBehindCountAsync(worktree, PullUpstream)).ReturnsAsync(behind);
    }

    [Fact]
    public async Task PullBranch_rebases_onto_the_remote_counterpart_with_the_repository_credentials()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;
        var (id, worktree) = await SeedWorktreeWorkItemAsync(mgr, db, repoId);
        try
        {
            SetupPullable(repoMgr, worktree, behind: 2);
            GitAuthOptions? fetchAuth = null;
            repoMgr.Setup(r => r.FetchAsync(worktree, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .Callback<string, CancellationToken, GitAuthOptions?>((_, _, auth) => fetchAuth = auth)
                .ReturnsAsync(true);
            repoMgr.Setup(r => r.RebaseAsync(worktree, PullUpstream, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RebaseResult(true, Array.Empty<string>(), null));

            var result = await mgr.PullBranchAsync(id);

            Assert.Equal(PullBranchOutcome.Updated, result.Outcome);
            Assert.True(result.Success);
            Assert.Equal(PullBranchName, result.Branch);
            Assert.Contains("2 new commits", result.Message);
            Assert.Empty(result.Files);

            // The fetch is the only step that talks to the remote, so it is the
            // only one that may carry the repository's credentials.
            Assert.NotNull(fetchAuth);
            Assert.Equal("https://example/repo.git", fetchAuth!.RemoteUrl);
            Assert.Equal("Forgejo", fetchAuth.ProviderType);
            repoMgr.Verify(r => r.RebaseAsync(worktree, PullUpstream, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task PullBranch_reports_already_up_to_date_without_rebasing()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;
        var (id, worktree) = await SeedWorktreeWorkItemAsync(mgr, db, repoId);
        try
        {
            SetupPullable(repoMgr, worktree, behind: 0);

            var result = await mgr.PullBranchAsync(id);

            Assert.Equal(PullBranchOutcome.AlreadyUpToDate, result.Outcome);
            Assert.True(result.Success);
            repoMgr.Verify(r => r.RebaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task PullBranch_is_an_explicit_no_op_when_the_branch_was_never_pushed()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;
        var (id, worktree) = await SeedWorktreeWorkItemAsync(mgr, db, repoId);
        try
        {
            SetupPullable(repoMgr, worktree, behind: 0);
            repoMgr.Setup(r => r.RemoteBranchExistsAsync(worktree, PullBranchName)).ReturnsAsync(false);

            var result = await mgr.PullBranchAsync(id);

            // Nothing to pull is not a failure — the branch simply has no remote
            // counterpart yet.
            Assert.Equal(PullBranchOutcome.NoRemoteBranch, result.Outcome);
            Assert.True(result.Success);
            Assert.Contains("not been pushed", result.Message);
            repoMgr.Verify(r => r.RebaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task PullBranch_refuses_a_dirty_worktree_naming_the_files_and_never_touches_the_remote()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;
        var (id, worktree) = await SeedWorktreeWorkItemAsync(mgr, db, repoId);
        try
        {
            SetupPullable(repoMgr, worktree, behind: 3);
            repoMgr.Setup(r => r.GetUncommittedFilesAsync(worktree))
                .ReturnsAsync(new[] { "src/App.tsx", "README.md" });

            var result = await mgr.PullBranchAsync(id);

            Assert.Equal(PullBranchOutcome.DirtyWorktree, result.Outcome);
            Assert.False(result.Success);
            Assert.Equal(new[] { "src/App.tsx", "README.md" }, result.Files);
            Assert.Contains("src/App.tsx", result.Message);
            Assert.Contains("README.md", result.Message);
            // Fails fast: no network call, and above all no stash — silently
            // stashing would swallow the agent's in-flight work.
            repoMgr.Verify(r => r.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
            repoMgr.Verify(r => r.RebaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task PullBranch_returns_the_conflicted_files_when_the_rebase_fails()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;
        var (id, worktree) = await SeedWorktreeWorkItemAsync(mgr, db, repoId);
        try
        {
            SetupPullable(repoMgr, worktree, behind: 1);
            repoMgr.Setup(r => r.RebaseAsync(worktree, PullUpstream, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RebaseResult(false, new[] { "src/App.tsx" }, "CONFLICT (content)"));

            var result = await mgr.PullBranchAsync(id);

            Assert.Equal(PullBranchOutcome.Conflict, result.Outcome);
            Assert.False(result.Success);
            Assert.Equal(new[] { "src/App.tsx" }, result.Files);
            Assert.Contains("src/App.tsx", result.Message);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task PullBranch_separates_a_refused_rebase_from_a_conflicted_one()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;
        var (id, worktree) = await SeedWorktreeWorkItemAsync(mgr, db, repoId);
        try
        {
            SetupPullable(repoMgr, worktree, behind: 1);
            // Git refused before it started — there are no conflicted files, so
            // reporting a conflict would send the caller hunting for markers that
            // do not exist.
            repoMgr.Setup(r => r.RebaseAsync(worktree, PullUpstream, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RebaseResult(false, Array.Empty<string>(),
                    "error: untracked working tree files would be overwritten"));

            var result = await mgr.PullBranchAsync(id);

            Assert.Equal(PullBranchOutcome.RebaseRefused, result.Outcome);
            Assert.False(result.Success);
            Assert.Empty(result.Files);
            Assert.Contains("untracked working tree files would be overwritten", result.Message);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task PullBranch_fails_when_the_fetch_fails_and_never_rebases()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;
        var (id, worktree) = await SeedWorktreeWorkItemAsync(mgr, db, repoId);
        try
        {
            SetupPullable(repoMgr, worktree, behind: 1);
            repoMgr.Setup(r => r.FetchAsync(worktree, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(false);

            var result = await mgr.PullBranchAsync(id);

            Assert.Equal(PullBranchOutcome.Failed, result.Outcome);
            Assert.False(result.Success);
            // A stale remote-tracking ref must not be rebased onto as if it were
            // current — the same invariant the Start node enforces.
            repoMgr.Verify(r => r.RebaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task PullBranch_fails_when_the_work_item_has_no_worktree()
    {
        var (mgr, db, repoId, repoMgr, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        SeedLoopRun(db, id); // run has no WorktreePath

        var result = await mgr.PullBranchAsync(id);

        Assert.Equal(PullBranchOutcome.Failed, result.Outcome);
        Assert.Null(result.Branch);
        Assert.Empty(result.Files);
        repoMgr.Verify(r => r.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
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

    // ---- MergePullRequestAsync -------------------------------------------

    [Fact]
    public async Task MergePullRequest_merges_deletes_branch_and_advances_OnSuccess()
    {
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.MergePullRequestAsync(MergeRepoCloneUrl, "42")).ReturnsAsync(true);
        remote.Setup(r => r.DeleteBranchAsync(MergeRepoCloneUrl, "ild/wi-x-run-1")).ReturnsAsync(true);
        var (mgr, db, repoId, _, _) = SetupCore(out var engine, remote);
        using var _ = db;

        var (id, runId, runNodeId) = SeedPrAwaitingMerge(
            mgr, db, repoId, "https://example/repo/pulls/42", "ild/wi-x-run-1");

        var result = await mgr.MergePullRequestAsync(id, deleteBranch: true);

        Assert.NotNull(result);
        Assert.True(result!.Merged);
        Assert.True(result.BranchDeleted);
        Assert.Null(result.BranchWarning);
        remote.Verify(r => r.MergePullRequestAsync(MergeRepoCloneUrl, "42"), Times.Once);
        remote.Verify(r => r.DeleteBranchAsync(MergeRepoCloneUrl, "ild/wi-x-run-1"), Times.Once);
        // OnSuccess continuation is identical to Approve: signal Success on the parked node.
        engine.Verify(eng => eng.SignalNodeResultAsync(runId, runNodeId,
            It.Is<NodeSignal>(s => s.Type == ExternalActionResultType.Success)), Times.Once);
    }

    [Fact]
    public async Task MergePullRequest_does_not_delete_branch_when_not_requested()
    {
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.MergePullRequestAsync(MergeRepoCloneUrl, "7")).ReturnsAsync(true);
        var (mgr, db, repoId, _, _) = SetupCore(out var engine, remote);
        using var _ = db;

        var (id, runId, runNodeId) = SeedPrAwaitingMerge(
            mgr, db, repoId, "https://example/repo/pulls/7", "ild/wi-x-run-2");

        var result = await mgr.MergePullRequestAsync(id, deleteBranch: false);

        Assert.True(result!.Merged);
        Assert.False(result.BranchDeleted);
        remote.Verify(r => r.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        engine.Verify(eng => eng.SignalNodeResultAsync(runId, runNodeId,
            It.Is<NodeSignal>(s => s.Type == ExternalActionResultType.Success)), Times.Once);
    }

    [Fact]
    public async Task MergePullRequest_failed_merge_leaves_item_parked_and_does_not_advance()
    {
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.MergePullRequestAsync(MergeRepoCloneUrl, "9")).ReturnsAsync(false);
        var (mgr, db, repoId, _, _) = SetupCore(out var engine, remote);
        using var _ = db;

        var (id, _, _) = SeedPrAwaitingMerge(
            mgr, db, repoId, "https://example/repo/pulls/9", "ild/wi-x-run-3");

        var result = await mgr.MergePullRequestAsync(id, deleteBranch: true);

        Assert.NotNull(result);
        Assert.False(result!.Merged);
        Assert.NotNull(result.Error);
        // No branch delete and no loop continuation when the merge fails.
        remote.Verify(r => r.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        engine.Verify(eng => eng.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task MergePullRequest_branch_delete_failure_is_reported_but_does_not_block_continuation()
    {
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.MergePullRequestAsync(MergeRepoCloneUrl, "11")).ReturnsAsync(true);
        remote.Setup(r => r.DeleteBranchAsync(MergeRepoCloneUrl, "ild/wi-x-run-4")).ReturnsAsync(false);
        var (mgr, db, repoId, _, _) = SetupCore(out var engine, remote);
        using var _ = db;

        var (id, runId, runNodeId) = SeedPrAwaitingMerge(
            mgr, db, repoId, "https://example/repo/pulls/11", "ild/wi-x-run-4");

        var result = await mgr.MergePullRequestAsync(id, deleteBranch: true);

        Assert.True(result!.Merged);
        Assert.False(result.BranchDeleted);
        Assert.NotNull(result.BranchWarning);
        // The merge succeeded, so the loop still advances along OnSuccess.
        engine.Verify(eng => eng.SignalNodeResultAsync(runId, runNodeId,
            It.Is<NodeSignal>(s => s.Type == ExternalActionResultType.Success)), Times.Once);
    }

    [Fact]
    public async Task MergePullRequest_returns_null_for_unknown_workitem()
    {
        var remote = new Mock<IRemoteProvider>();
        var (mgr, db, _, _, _) = SetupCore(out var engine, remote);
        using var dispose = db;

        Assert.Null(await mgr.MergePullRequestAsync(Guid.NewGuid().ToString(), deleteBranch: true));
        engine.Verify(eng => eng.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
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
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
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

    // ---- Preview teardown on Done ----------------------------------------

    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IWorktreePreviewService> preview, Mock<IWorkItemNotifier> notifier, Mock<ILoopEngine> engine) SetupWithPreview()
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();
        var repoMgr = new Mock<IRepositoryManager>();
        var eventLog = new Mock<IEventLogService>();
        var notifier = new Mock<IWorkItemNotifier>();
        var preview = new Mock<IWorktreePreviewService>();
        var engine = new Mock<ILoopEngine>();
        var mgr = new WorkItemManager(repoMgr.Object, db.Providers, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions,
            notifier.Object, preview.Object, engine: engine.Object);
        return (mgr, db, repo.Id, preview, notifier, engine);
    }

    [Fact]
    public async Task TransitionToDoneAsync_ends_the_run_parked_behind_the_item()
    {
        // Dragging a card to Done, or picking Done from the status menu, is a
        // human saying the work is over. The run has to end with it: the
        // scheduler's Active Work Item Set is derived from live runs, so a run
        // left parked at a human gate under a Done card would be heartbeated
        // and hold a concurrency slot for the lifetime of the process.
        var (mgr, db, repoId, _, notifier, engine) = SetupWithPreview();
        using var _d = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = SeedLoopRun(db, id);
        var parked = await db.Context.LoopRuns.FindAsync(runId);
        parked!.Status = LoopRunStatus.WaitingHuman;
        await db.Context.SaveChangesAsync();

        await mgr.TransitionToDoneAsync(id);

        var after = db.Fresh().LoopRuns.First(r => r.Id == runId);
        Assert.NotEqual(LoopRunStatus.WaitingHuman, after.Status);
        Assert.NotNull(after.CompletedAt);
        // And so it no longer holds a slot or a heartbeat.
        Assert.DoesNotContain(id, await db.LoopRuns.GetActiveWorkItemIdsAsync());

        // Stopped, not cancelled: CancelRunAsync parks the work item in
        // HumanFeedback on the way past, which would leave a "Run cancelled"
        // message in the conversation of an item the human just finished and
        // pop a needs-attention toast on its card.
        engine.Verify(e => e.StopRunAsync(runId, It.IsAny<string>()), Times.Once);
        engine.Verify(e => e.CancelRunAsync(It.IsAny<Guid>()), Times.Never);
        notifier.Verify(n => n.WorkItemStateChangedAsync(
            id, It.IsAny<RemoteWorkItemStatus>(), RemoteWorkItemStatus.HumanFeedback), Times.Never);
    }

    [Fact]
    public async Task TransitionAsync_to_Done_stops_running_preview_and_notifies()
    {
        var (mgr, db, repoId, preview, notifier, _) = SetupWithPreview();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = SeedLoopRun(db, id);
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.WorktreePath = "/tmp/worktrees/done-wi";
        await db.Context.SaveChangesAsync();

        preview.Setup(p => p.IsPreviewRunning("/tmp/worktrees/done-wi")).Returns(true);

        await mgr.TransitionToDoneAsync(id);

        preview.Verify(p => p.StopAsync("/tmp/worktrees/done-wi", It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.PreviewStateChangedAsync(id), Times.Once);
    }

    [Fact]
    public async Task TransitionAsync_to_Done_does_not_stop_when_no_preview_running()
    {
        var (mgr, db, repoId, preview, notifier, _) = SetupWithPreview();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = SeedLoopRun(db, id);
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.WorktreePath = "/tmp/worktrees/done-wi";
        await db.Context.SaveChangesAsync();

        preview.Setup(p => p.IsPreviewRunning(It.IsAny<string>())).Returns(false);

        await mgr.TransitionToDoneAsync(id);

        preview.Verify(p => p.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        notifier.Verify(n => n.PreviewStateChangedAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TransitionAsync_to_non_Done_leaves_running_preview_alone()
    {
        var (mgr, db, repoId, preview, _, _) = SetupWithPreview();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var runId = SeedLoopRun(db, id);
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.WorktreePath = "/tmp/worktrees/running-wi";
        await db.Context.SaveChangesAsync();

        preview.Setup(p => p.IsPreviewRunning(It.IsAny<string>())).Returns(true);

        // Moving to Running (e.g. resuming) must not tear down the preview.
        await mgr.TransitionAsync(id, RemoteWorkItemStatus.Running);

        preview.Verify(p => p.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupToDoneAsync_stops_running_preview()
    {
        var (mgr, db, repoId, preview, notifier, _) = SetupWithPreview();
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
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Failed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            WorktreePath = "/tmp/worktrees/cleanup-wi",
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        preview.Setup(p => p.IsPreviewRunning("/tmp/worktrees/cleanup-wi")).Returns(true);

        await mgr.CleanupToDoneAsync(id);

        preview.Verify(p => p.StopAsync("/tmp/worktrees/cleanup-wi", It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.PreviewStateChangedAsync(id), Times.Once);
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

    private static string SerializePrSnapshot(RemotePrSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web);

    [Fact]
    public async Task GetWorkItemAsync_leaves_PrStatus_null_when_run_has_no_snapshot()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        SeedRunWithVersion(db, id);

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Null(wi!.PrStatus);
    }

    [Fact]
    public async Task GetWorkItemAsync_projects_PrStatus_from_run_snapshot()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var (runId, _) = SeedRunWithVersion(db, id);
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.PrSnapshot = SerializePrSnapshot(new RemotePrSnapshot(
            Title: "Add feature",
            Body: "body",
            State: "open",
            Merged: false,
            Mergeable: false,
            MergeableState: "dirty",
            Ci: RemotePrCiStatus.Passed,
            Approved: true,
            ChangesRequested: true,
            Conversation: Array.Empty<RemotePrConversationEntry>(),
            FetchedAt: DateTime.UtcNow));
        await db.Context.SaveChangesAsync();

        // Only the badge-relevant fields are projected — title/body/conversation
        // are intentionally dropped so the board card stays lightweight.
        var status = (await mgr.GetWorkItemAsync(id))!.PrStatus;
        Assert.NotNull(status);
        Assert.Equal("open", status!.State);
        Assert.False(status.Merged);
        Assert.False(status.Mergeable);
        Assert.Equal("dirty", status.MergeableState);
        Assert.Equal(RemotePrCiStatus.Passed, status.Ci);
        Assert.True(status.Approved);
        Assert.True(status.ChangesRequested);
    }

    [Fact]
    public async Task GetWorkItemAsync_degrades_corrupt_PrSnapshot_to_null_PrStatus()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var (runId, _) = SeedRunWithVersion(db, id);
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.PrSnapshot = "{ not valid json";
        await db.Context.SaveChangesAsync();

        var wi = await mgr.GetWorkItemAsync(id);
        Assert.Null(wi!.PrStatus);
    }

    // ──────────────────────────────────────────────────────────────────
    // PR history — WI-203 "PR disappears"
    //
    // A work item has no PR field of its own: WorkItemView.PrUrl is projected
    // from the *current* run, and "current" excludes Completed runs. So the
    // moment the engine finishes a run (CompleteRunAsync marks it Completed and
    // moves the item to Done) the PR link, branch and badges vanish from the
    // view even though the LoopRun row still carries the URL. Later the
    // retention sweeper deletes that row outright and the link is gone for good.
    //
    // The contract these tests pin: WorkItemView.PullRequests carries every PR
    // ever opened against the item — newest run first, deduplicated by URL —
    // and survives both the run going terminal and the run row being deleted.
    // PrUrl is left alone (ADR-0002: /api/v1 is add-only).
    // ──────────────────────────────────────────────────────────────────

    private static Guid SeedTemplateVersion(TestDb db)
    {
        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "pr-history" };
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplates.Add(lt);
        db.Context.LoopTemplateVersions.Add(ltv);
        db.Context.SaveChanges();
        return ltv.Id;
    }

    /// <summary>
    /// One run of <paramref name="workItemId"/> carrying <paramref name="prUrl"/>.
    /// <paramref name="startedAt"/> is explicit because it is what orders PR
    /// history — pass distinct values to pin an order deterministically. A
    /// Running run is left without a completion timestamp: AppDbContext refuses
    /// to persist "running yet completed".
    /// </summary>
    private static Guid SeedRunWithPr(
        TestDb db,
        Guid ltvId,
        string workItemId,
        string prUrl,
        LoopRunStatus status,
        DateTime startedAt,
        bool merged = false,
        string? prSnapshot = null)
    {
        var runId = Guid.NewGuid();
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = runId,
            WorkItemId = workItemId,
            LoopTemplateVersionId = ltvId,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status == LoopRunStatus.Running ? null : startedAt.AddMinutes(5),
            PrUrl = prUrl,
            IsPrMerged = merged,
            PrSnapshot = prSnapshot,
            BranchName = $"ild/{workItemId}-run-{runId}",
        });
        db.Context.SaveChanges();
        return runId;
    }

    /// <summary>
    /// Finish a run the way <c>LoopEngine.CompleteRunAsync</c> does — the run
    /// goes Completed and the work item goes Done — which is the exact sequence
    /// that makes the PR disappear today.
    /// </summary>
    private static async Task CompleteRunAndFinishItemAsync(WorkItemManager mgr, TestDb db, Guid runId, string workItemId)
    {
        var run = await db.Context.LoopRuns.FindAsync(runId);
        run!.Status = LoopRunStatus.Completed;
        run.CompletedAt = DateTime.UtcNow;
        await db.Context.SaveChangesAsync();
        await mgr.TransitionAsync(workItemId, RemoteWorkItemStatus.Done, currentLoopRunId: runId);
    }

    [Fact]
    public async Task PrHistory_survives_the_run_completing_and_the_item_going_Done()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        var id = await mgr.CreateWorkItemAsync("ship it", "", repoId);
        var runId = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/1", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        // While the run is alive the PR is visible both ways.
        var before = await mgr.GetWorkItemAsync(id);
        Assert.Equal("https://forgejo/repo/pulls/1", before!.PrUrl);
        Assert.Equal(new[] { "https://forgejo/repo/pulls/1" }, before.PullRequests.Select(p => p.Url));

        await CompleteRunAndFinishItemAsync(mgr, db, runId, id);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Done, after!.Status);
        Assert.Equal(new[] { "https://forgejo/repo/pulls/1" }, after.PullRequests.Select(p => p.Url));
    }

    [Fact]
    public async Task PrHistory_survives_CleanupToDoneAsync()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        var id = await mgr.CreateWorkItemAsync("clean me up", "", repoId);
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/7", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        Assert.True(await mgr.CleanupToDoneAsync(id));

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(RemoteWorkItemStatus.Done, after!.Status);
        Assert.Equal(new[] { "https://forgejo/repo/pulls/7" }, after.PullRequests.Select(p => p.Url));
    }

    [Fact]
    public async Task PrHistory_lists_every_PR_across_sequential_runs_newest_first()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        // Three attempts at the same item, each (per ADR-0008) with its own run,
        // branch and PR. All three PRs belong to the item's history.
        var id = await mgr.CreateWorkItemAsync("third time lucky", "", repoId);
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/10", LoopRunStatus.Completed, DateTime.UtcNow.AddHours(-3));
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/11", LoopRunStatus.Failed, DateTime.UtcNow.AddHours(-2));
        var newest = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/12", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        await CompleteRunAndFinishItemAsync(mgr, db, newest, id);

        // Newest run first — the order the detail view renders, so the PR a
        // human most likely wants is at the top.
        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(
            new[]
            {
                "https://forgejo/repo/pulls/12",
                "https://forgejo/repo/pulls/11",
                "https://forgejo/repo/pulls/10",
            },
            after!.PullRequests.Select(p => p.Url));
    }

    [Fact]
    public async Task PrHistory_dedupes_a_PR_url_carried_by_more_than_one_run()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        // A retried run can be pointed back at the PR its predecessor opened
        // (PrSyncService and the manual Link PR action both write the URL onto
        // whatever run is current). The same URL is one PR, listed once.
        var id = await mgr.CreateWorkItemAsync("retried", "", repoId);
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/5", LoopRunStatus.Failed, DateTime.UtcNow.AddHours(-3));
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/9", LoopRunStatus.Completed, DateTime.UtcNow.AddHours(-2));
        var newest = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/5", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        await CompleteRunAndFinishItemAsync(mgr, db, newest, id);

        var history = (await mgr.GetWorkItemAsync(id))!.PullRequests;
        Assert.Equal(
            new[] { "https://forgejo/repo/pulls/5", "https://forgejo/repo/pulls/9" },
            history.Select(p => p.Url));
        // The surviving entry is the newest sighting of that URL.
        Assert.Equal(newest, history[0].RunId);
    }

    [Fact]
    public async Task ListAsync_exposes_the_same_PR_history_as_GetWorkItemAsync()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        // The taskboard reads ListAsync, the detail dialog reads
        // GetWorkItemAsync — both project the current run and both drop the PR
        // the same way, so both have to be pinned.
        var id = await mgr.CreateWorkItemAsync("board and dialog agree", "", repoId);
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/20", LoopRunStatus.Completed, DateTime.UtcNow.AddHours(-2));
        var newest = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/21", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        await CompleteRunAndFinishItemAsync(mgr, db, newest, id);

        var listed = (await mgr.ListAsync(null, null, null, 0, 100)).Single(v => v.Id == id);
        var fetched = (await mgr.GetWorkItemAsync(id))!;
        Assert.Equal(
            new[] { "https://forgejo/repo/pulls/21", "https://forgejo/repo/pulls/20" },
            listed.PullRequests.Select(p => p.Url));
        Assert.Equal(fetched.PullRequests.Select(p => p.Url), listed.PullRequests.Select(p => p.Url));
    }

    [Fact]
    public async Task PrHistory_survives_deletion_of_the_LoopRun_row()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        var id = await mgr.CreateWorkItemAsync("swept", "", repoId);
        var runId = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/33", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));
        await CompleteRunAndFinishItemAsync(mgr, db, runId, id);

        // Reading the item before the sweep is deliberate, not incidental: in
        // production a Done item is viewed many times over the days before
        // RunRetentionDays elapses, so recording PR history lazily on read is a
        // legitimate implementation. What is *not* legitimate is aggregating
        // over live LoopRun rows — WorktreeRetentionSweeper hard-deletes
        // non-Retain terminal runs past the retention window, and "forever" has
        // to outlive that.
        Assert.Equal(new[] { "https://forgejo/repo/pulls/33" }, (await mgr.GetWorkItemAsync(id))!.PullRequests.Select(p => p.Url));

        Assert.True(await db.LoopRuns.DeleteAsync(runId));
        Assert.Empty(db.Fresh().LoopRuns.Where(r => r.WorkItemId == id));

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(new[] { "https://forgejo/repo/pulls/33" }, after!.PullRequests.Select(p => p.Url));
    }

    [Fact]
    public async Task PrHistory_entries_carry_the_run_merged_flag_and_badge_status()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        var id = await mgr.CreateWorkItemAsync("merged one", "", repoId);
        var startedAt = DateTime.UtcNow.AddHours(-1);
        var runId = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/44", LoopRunStatus.Running, startedAt,
            merged: true,
            prSnapshot: SerializePrSnapshot(new RemotePrSnapshot(
                Title: "Add feature",
                Body: "body",
                State: "closed",
                Merged: true,
                Mergeable: null,
                MergeableState: "clean",
                Ci: RemotePrCiStatus.Passed,
                Approved: true,
                ChangesRequested: false,
                Conversation: Array.Empty<RemotePrConversationEntry>(),
                FetchedAt: DateTime.UtcNow)));

        await CompleteRunAndFinishItemAsync(mgr, db, runId, id);

        // A history entry has to stand on its own: the run it came from is on
        // its way to being reclaimed, so the link, who opened it, whether it
        // landed and its last known badge state all live on the entry.
        var pr = Assert.Single((await mgr.GetWorkItemAsync(id))!.PullRequests);
        Assert.Equal("https://forgejo/repo/pulls/44", pr.Url);
        Assert.Equal(runId, pr.RunId);
        Assert.True(pr.Merged);
        Assert.NotNull(pr.Status);
        Assert.Equal("closed", pr.Status!.State);
        Assert.Equal(RemotePrCiStatus.Passed, pr.Status.Ci);
        Assert.NotNull(pr.CreatedAt);
    }

    [Fact]
    public async Task PrUrl_keeps_meaning_the_current_runs_PR_once_history_exists()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        // /api/v1 is add-only (ADR-0002): PR history is a new collection, and
        // prUrl must keep pointing at the current run's PR — never at the
        // newest history entry — so existing clients see no behaviour change.
        var id = await mgr.CreateWorkItemAsync("add only", "", repoId);
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/60", LoopRunStatus.Completed, DateTime.UtcNow.AddHours(-2));
        var current = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/61", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        Assert.Equal("https://forgejo/repo/pulls/61", (await mgr.GetWorkItemAsync(id))!.PrUrl);

        await CompleteRunAndFinishItemAsync(mgr, db, current, id);

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Null(after!.PrUrl);
        Assert.Equal(
            new[] { "https://forgejo/repo/pulls/61", "https://forgejo/repo/pulls/60" },
            after.PullRequests.Select(p => p.Url));
    }

    // ──────────────────────────────────────────────────────────────────
    // PR history lives on the work item, i.e. on the WorkItem server — not in
    // this ILD instance's database. A worktree, a branch and a PR snapshot are
    // throwaway; the PR touches the repository and is part of the work item.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrHistory_survives_the_ILD_instance_being_reset()
    {
        using var server = new FakeWorkItemServerHarness();
        var (mgr, db, repoId, _, _) = Setup(server);
        var ltvId = SeedTemplateVersion(db);

        var id = await mgr.CreateWorkItemAsync("outlives its instance", "", repoId);
        var runId = SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/77", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));
        await CompleteRunAndFinishItemAsync(mgr, db, runId, id);
        Assert.Equal(new[] { "https://forgejo/repo/pulls/77" }, (await mgr.GetWorkItemAsync(id))!.PullRequests.Select(p => p.Url));

        // Wipe the ILD instance: its database goes, taking every LoopRun,
        // worktree and branch with it. The work-item server keeps running.
        db.Dispose();
        using var restarted = new TestDb(server);
        var freshMgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object, restarted.Providers, new Mock<IEventLogService>().Object,
            restarted.LoopRuns, restarted.ServerClient, restarted.ServerOptions);

        Assert.Empty(restarted.Context.LoopRuns);
        var view = (await freshMgr.GetWorkItemAsync(id))!;
        Assert.Equal(new[] { "https://forgejo/repo/pulls/77" }, view.PullRequests.Select(p => p.Url));
        // Nothing local is left to attribute it to, and the run-scoped fields
        // are gone with the instance — the PR link is not.
        Assert.Null(view.PullRequests[0].RunId);
        Assert.Null(view.PrUrl);
        Assert.Null(view.WorktreePath);
    }

    [Fact]
    public async Task Linking_a_PR_by_hand_works_on_a_Done_item_with_no_current_run()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        // No run at all: the item is finished, and a human is recording the PR
        // that came out of it. The link belongs to the item, so it does not
        // need a run to hang on.
        var id = await mgr.CreateWorkItemAsync("link it after the fact", "", repoId);
        await mgr.TransitionAsync(id, RemoteWorkItemStatus.Done);

        Assert.True(await mgr.LinkPullRequestAsync(id, "https://forgejo/repo/pulls/88"));

        var after = await mgr.GetWorkItemAsync(id);
        Assert.Equal(new[] { "https://forgejo/repo/pulls/88" }, after!.PullRequests.Select(p => p.Url));
        // prUrl is the current run's PR and there is no run — unchanged (ADR-0002).
        Assert.Null(after.PrUrl);
    }

    [Fact]
    public async Task A_work_item_carrying_the_same_PR_twice_still_reads()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        var id = await mgr.CreateWorkItemAsync("duplicated", "", repoId);
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/5", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        // The upsert never writes a URL twice, but data that predates it — or
        // that someone edited by hand — can. Reading the work item is not the
        // place to find out: it would take the whole taskboard down with it.
        var row = await db.Server.ServerDb.WorkItems.FirstAsync(w => w.Id == id);
        row.PullRequestsJson =
            """
            [{"url":"https://forgejo/repo/pulls/5","loopRunId":null,"merged":false,"createdAt":"2026-04-01T00:00:00Z"},
             {"url":"https://forgejo/repo/pulls/5","loopRunId":null,"merged":true,"createdAt":"2026-04-01T00:00:00Z"}]
            """;
        await db.Server.ServerDb.SaveChangesAsync();

        var pr = Assert.Single((await mgr.GetWorkItemAsync(id))!.PullRequests);
        Assert.Equal("https://forgejo/repo/pulls/5", pr.Url);
        // Collapsed to one entry, keeping what the duplicates knew between them.
        Assert.True(pr.Merged);
        Assert.Single((await mgr.ListAsync(null, null, null, 0, 100)).Single(v => v.Id == id).PullRequests);
    }

    [Fact]
    public async Task A_PR_the_server_already_holds_is_not_re_reported_on_every_read()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;
        var ltvId = SeedTemplateVersion(db);

        var id = await mgr.CreateWorkItemAsync("read me twice", "", repoId);
        SeedRunWithPr(db, ltvId, id, "https://forgejo/repo/pulls/99", LoopRunStatus.Running, DateTime.UtcNow.AddHours(-1));

        await mgr.GetWorkItemAsync(id);
        var afterFirstRead = (await db.Server.Service.GetAsync(id))!.UpdatedAt;
        await mgr.GetWorkItemAsync(id);
        await mgr.ListAsync(null, null, null, 0, 100);

        // Reads reconcile what the runs still carry, but a PR the item already
        // records is left alone — the taskboard polls this path continuously.
        var pr = Assert.Single((await db.Server.Service.GetAsync(id))!.PullRequests);
        Assert.Equal("https://forgejo/repo/pulls/99", pr.Url);
        Assert.Equal(afterFirstRead, (await db.Server.Service.GetAsync(id))!.UpdatedAt);
    }
}
