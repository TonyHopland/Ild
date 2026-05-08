using FluentAssertions;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

public class WorkItemManagerTests
{
    private static Guid SeedLoopRun(TestDb db, Guid workItemId)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 100, MaxWallClockHours = 1 };
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

    private static (WorkItemManager mgr, TestDb db, Guid repoId, Mock<IRepositoryManager> repoMgr, Mock<IEventLogService> eventLog) Setup()
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
        return (new WorkItemManager(db.WorkItems, repoMgr.Object, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions), db, repo.Id, repoMgr, eventLog);
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
        wi!.Status.Should().Be(WorkItemStatus.WorkQueue);
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
        wi!.Status.Should().Be(WorkItemStatus.Backlog);
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
        ok.Should().BeTrue();

        var reloaded = await mgr.GetWorkItemAsync(id);
        reloaded!.Title.Should().Be("new title");
        reloaded.Description.Should().Be("new desc");
        reloaded.UpdatedAt.Should().NotBe(before);
    }

    [Fact]
    public async Task UpdateAsync_returns_false_for_unknown_id()
    {
        var (mgr, db, _, _, _) = Setup();
        using var _ = db;

        (await mgr.UpdateAsync(Guid.NewGuid(), "t", "d")).Should().BeFalse();
    }

    [Fact]
    public async Task IsReady_true_for_workitem_with_no_dependencies()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        await mgr.TransitionToWorkQueueAsync(id);

        (await mgr.IsReadyAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task IsReady_false_when_dependency_not_merged()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);

        (await mgr.IsReadyAsync(child)).Should().BeFalse();
    }

    [Fact]
    public async Task IsReady_true_after_dependency_marked_done()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", repoId);
        await mgr.AddDependencyAsync(child, dep);

        await mgr.LinkPullRequestAsync(dep, "https://forgejo/pr/1");
        await mgr.ManuallyMarkMergedAsync(dep);

        (await mgr.IsReadyAsync(child)).Should().BeTrue();
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
        var depWi = await db.Context.WorkItems.FindAsync(dep);
        depWi!.IsPrMerged = true;
        depWi.Status = WorkItemStatus.HumanFeedback;
        await db.Context.SaveChangesAsync();

        (await mgr.IsReadyAsync(child)).Should().BeFalse();
    }

    [Fact]
    public async Task AddDependency_rejects_self_loop()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var act = async () => await mgr.AddDependencyAsync(id, id);
        await act.Should().ThrowAsync<InvalidOperationException>();
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
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cycle*");
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
        transitioned.Should().BeFalse();
        var wi = await mgr.GetWorkItemAsync(child);
        wi!.Status.Should().Be(WorkItemStatus.WorkQueue);
    }

    [Fact]
    public async Task LinkPullRequest_persists_pr_url()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var result = await mgr.LinkPullRequestAsync(id, "https://forgejo/pr/42");

        result.Should().BeTrue();
        var wi = await mgr.GetWorkItemAsync(id);
        wi!.PrUrl.Should().Be("https://forgejo/pr/42");
    }

    [Fact]
    public async Task ManuallyMarkMerged_transitions_workitem_to_Done()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.Running;
        await db.Context.SaveChangesAsync();

        await mgr.LinkPullRequestAsync(id, "https://forgejo/pr/2");
        await mgr.ManuallyMarkMergedAsync(id);

        var after = await mgr.GetWorkItemAsync(id);
        after!.Status.Should().Be(WorkItemStatus.Done);
        after.IsPrMerged.Should().BeTrue();
    }

    [Fact]
    public async Task TransitionToHumanFeedback_sets_reason_on_workitem()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        await mgr.TransitionToReadyAsync(id);
        await mgr.TransitionToRunningAsync(id);

        var ok = await mgr.TransitionToHumanFeedbackAsync(id, "PR Awaiting Merge");

        ok.Should().BeTrue();
        var wi = await mgr.GetWorkItemAsync(id);
        wi!.Status.Should().Be(WorkItemStatus.HumanFeedback);
        wi.HumanFeedbackReason.Should().Be("PR Awaiting Merge");
    }

    [Fact]
    public async Task ManuallyMarkMerged_does_not_set_Done_when_running_LoopRun_exists()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.Running;
        await db.Context.SaveChangesAsync();

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
        after!.IsPrMerged.Should().BeTrue();
        after.Status.Should().NotBe(WorkItemStatus.Done);
    }

    [Fact]
    public async Task ManuallyMarkMerged_sets_Done_when_LoopRun_is_Failed()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.Running;
        await db.Context.SaveChangesAsync();

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
        after!.IsPrMerged.Should().BeTrue();
        after.Status.Should().Be(WorkItemStatus.Done);
    }

    [Fact]
    public async Task CleanupToDone_destroys_worktree_and_marks_workitem_Done()
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
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.WorktreePath = "/tmp/worktrees/test-wi";
        wi.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = "Node Failed";
        await db.Context.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Failed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        await mgr.CleanupToDoneAsync(id);

        repoMgr.Verify(r => r.DestroyWorktreeAsync("/tmp/worktrees/test-wi"), Times.Once);
        var after = await mgr.GetWorkItemAsync(id);
        after!.Status.Should().Be(WorkItemStatus.Done);
        after.WorktreePath.Should().BeNullOrEmpty();
        after.HumanFeedbackReason.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CleanupToBacklog_destroys_worktree_resets_to_Backlog_and_clears_run_state()
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

        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.WorktreePath = "/tmp/worktrees/test-wi";
        wi.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = "Node Failed";
        wi.LoopTemplateVersionId = ltv.Id;
        wi.CurrentLoopRunId = runId;
        await db.Context.SaveChangesAsync();

        await mgr.CleanupToBacklogAsync(id);

        repoMgr.Verify(r => r.DestroyWorktreeAsync("/tmp/worktrees/test-wi"), Times.Once);
        var after = await mgr.GetWorkItemAsync(id);
        after!.Status.Should().Be(WorkItemStatus.Backlog);
        after.WorktreePath.Should().BeNullOrEmpty();
        after.HumanFeedbackReason.Should().BeNullOrEmpty();
        after.CurrentLoopRunId.Should().BeNull();
        after.BranchName.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task SubmitHumanFeedbackInput_finalizes_latest_Human_run_node_with_input_as_output()
    {
        var (mgr, db, repoId, _, _) = Setup();
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
        var runNodeId = Guid.NewGuid();
        db.Context.LoopRunNodes.Add(new LoopRunNode
        {
            Id = runNodeId,
            LoopRunId = runId,
            LoopNodeId = humanNodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        });

        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.HumanFeedback;
        wi.CurrentLoopRunId = runId;
        await db.Context.SaveChangesAsync();

        await mgr.SubmitHumanFeedbackInputAsync(id, "ship it");

        var finalized = await db.Fresh().LoopRunNodes.FindAsync(runNodeId);
        finalized!.Status.Should().Be(LoopRunNodeStatus.Succeeded);
        finalized.Output.Should().Be("ship it");
        finalized.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SubmitHumanFeedbackInput_appends_to_event_log_and_resumes_run()
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
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);

        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = "Human Input Needed";
        wi.CurrentLoopRunId = runId;
        await db.Context.SaveChangesAsync();

        await mgr.SubmitHumanFeedbackInputAsync(id, "proceed with the change");

        eventLog.Verify(e => e.AppendAsync(runId, "HumanFeedbackReceived", "proceed with the change", null), Times.Once);
        var after = await mgr.GetWorkItemAsync(id);
        after!.Status.Should().Be(WorkItemStatus.Running);
        after.HumanFeedbackReason.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task DeleteAsync_removes_workitem_with_no_dependencies()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", repoId);

        var ok = await mgr.DeleteAsync(id);

        ok.Should().BeTrue();
        (await mgr.GetWorkItemAsync(id)).Should().BeNull();
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

        ok.Should().BeTrue();
        (await mgr.GetWorkItemAsync(child)).Should().BeNull();
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

        ok.Should().BeTrue();
        (await mgr.GetWorkItemAsync(dep)).Should().BeNull();
        // Dependencies live on the WorkItemServer; delete propagates there.
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_unknown_id()
    {
        var (mgr, db, _, _, _) = Setup();
        using var _ = db;

        (await mgr.DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task RejectHumanFeedback_fails_current_node_and_resumes_run()
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

        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = "Human Input Needed";
        wi.CurrentLoopRunId = runId;
        await db.Context.SaveChangesAsync();

        await mgr.RejectHumanFeedbackAsync(id);

        eventLog.Verify(e => e.AppendAsync(runId, "HumanFeedbackReceived", "rejected by user", null), Times.Once);
        var afterNode = await db.Context.LoopRunNodes.FindAsync(runNode.Id);
        afterNode!.Status.Should().Be(LoopRunNodeStatus.Failed);
        var after = await mgr.GetWorkItemAsync(id);
        after!.Status.Should().Be(WorkItemStatus.Running);
        after.HumanFeedbackReason.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task RejectHumanFeedback_with_input_writes_text_to_run_node_output_and_event_log()
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

        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = ILD.Data.Enums.HumanFeedbackReasons.HumanInputNeeded;
        wi.CurrentLoopRunId = runId;
        await db.Context.SaveChangesAsync();

        await mgr.RejectHumanFeedbackAsync(id, "looks wrong, try again with smaller scope");

        eventLog.Verify(e => e.AppendAsync(
            runId, "HumanFeedbackReceived",
            "rejected by user: looks wrong, try again with smaller scope",
            null), Times.Once);
        var afterNode = await db.Context.LoopRunNodes.FindAsync(runNode.Id);
        afterNode!.Status.Should().Be(LoopRunNodeStatus.Failed);
        afterNode.Output.Should().Be("looks wrong, try again with smaller scope");
    }

    [Fact]
    public async Task SubmitHumanFeedbackRespond_sets_responded_status_and_resumes_run()
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

        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = "Human Input Needed";
        wi.CurrentLoopRunId = runId;
        await db.Context.SaveChangesAsync();

        await mgr.SubmitHumanFeedbackRespondAsync(id, "please revise the approach");

        eventLog.Verify(e => e.AppendAsync(runId, "HumanFeedbackReceived", "please revise the approach", null), Times.Once);
        var afterNode = await db.Context.LoopRunNodes.FindAsync(runNode.Id);
        afterNode!.Status.Should().Be(LoopRunNodeStatus.Responded);
        afterNode.Output.Should().Be("please revise the approach");
        var after = await mgr.GetWorkItemAsync(id);
        after!.Status.Should().Be(WorkItemStatus.Running);
        after.HumanFeedbackReason.Should().BeNullOrEmpty();
    }

    // ---- TransitionAsync (canonical generic transition) ------------------

    [Fact]
    public async Task TransitionAsync_returns_false_for_unknown_workitem()
    {
        var (mgr, db, _, _, _) = Setup();
        using var _ = db;

        (await mgr.TransitionAsync(Guid.NewGuid(), WorkItemStatus.Done)).Should().BeFalse();
    }

    [Fact]
    public async Task TransitionAsync_to_HumanFeedback_stores_reason_and_actions()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        await mgr.TransitionAsync(id, WorkItemStatus.HumanFeedback, "Need approval", "[\"approve\",\"reject\"]");

        var wi = await mgr.GetWorkItemAsync(id);
        wi!.Status.Should().Be(WorkItemStatus.HumanFeedback);
        wi.HumanFeedbackReason.Should().Be("Need approval");
        wi.HumanFeedbackActions.Should().Be("[\"approve\",\"reject\"]");
    }

    [Fact]
    public async Task TransitionAsync_to_non_HumanFeedback_clears_feedback_fields()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        await mgr.TransitionAsync(id, WorkItemStatus.HumanFeedback, "reason", "actions");
        await mgr.TransitionAsync(id, WorkItemStatus.Running);

        var wi = await mgr.GetWorkItemAsync(id);
        wi!.Status.Should().Be(WorkItemStatus.Running);
        wi.HumanFeedbackReason.Should().BeNull();
        wi.HumanFeedbackActions.Should().BeNull();
    }

    [Fact]
    public async Task TransitionAsync_currentLoopRunId_null_leaves_existing_value()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        var existingRunId = SeedLoopRun(db, id);
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.CurrentLoopRunId = existingRunId;
        await db.Context.SaveChangesAsync();

        await mgr.TransitionAsync(id, WorkItemStatus.Running, currentLoopRunId: null);

        var after = await mgr.GetWorkItemAsync(id);
        after!.CurrentLoopRunId.Should().Be(existingRunId);
    }

    [Fact]
    public async Task TransitionAsync_currentLoopRunId_GuidEmpty_clears_value()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        var runId = SeedLoopRun(db, id);
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.CurrentLoopRunId = runId;
        await db.Context.SaveChangesAsync();

        await mgr.TransitionAsync(id, WorkItemStatus.Done, currentLoopRunId: Guid.Empty);

        var after = await mgr.GetWorkItemAsync(id);
        after!.CurrentLoopRunId.Should().BeNull();
    }

    [Fact]
    public async Task TransitionAsync_currentLoopRunId_value_sets_field()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("t", "", repoId);
        var newRunId = SeedLoopRun(db, id);

        await mgr.TransitionAsync(id, WorkItemStatus.Running, currentLoopRunId: newRunId);

        var after = await mgr.GetWorkItemAsync(id);
        after!.CurrentLoopRunId.Should().Be(newRunId);
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
        var mgr = new WorkItemManager(db.WorkItems, repoMgr.Object, eventLog.Object, db.LoopRuns, db.ServerClient, db.ServerOptions, notifier.Object);

        var id = await mgr.CreateWorkItemAsync("t", "", repo.Id);
        notifier.Invocations.Clear();

        // Same-status transition: no state-change notification, but HumanFeedback
        // notification still fires when transitioning to HumanFeedback with a reason.
        await mgr.TransitionAsync(id, WorkItemStatus.HumanFeedback, "first");
        await mgr.TransitionAsync(id, WorkItemStatus.HumanFeedback, "second");

        notifier.Verify(n => n.WorkItemStateChangedAsync(id, It.IsAny<WorkItemStatus>(), WorkItemStatus.HumanFeedback), Times.Once);
        notifier.Verify(n => n.HumanFeedbackRequiredAsync(id, "first"), Times.Once);
        notifier.Verify(n => n.HumanFeedbackRequiredAsync(id, "second"), Times.Once);
    }
}
