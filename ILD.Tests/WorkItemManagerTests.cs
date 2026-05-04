using FluentAssertions;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Moq;

namespace ILD.Tests;

public class WorkItemManagerTests
{
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
        return (new WorkItemManager(db.WorkItems, repoMgr.Object, eventLog.Object, db.LoopRuns), db, repo.Id, repoMgr, eventLog);
    }

    [Fact]
    public async Task CreateWorkItem_lands_in_WorkQueue_when_repo_default_is_WorkQueue()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var repo = await db.Context.Repositories.FindAsync(repoId);
        repo!.DefaultIntakeStatus = WorkItemStatus.WorkQueue;
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("title", "desc", null, repoId);

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

        var id = await mgr.CreateWorkItemAsync("title", "desc", null, repoId);

        var wi = await mgr.GetWorkItemAsync(id);
        wi!.Status.Should().Be(WorkItemStatus.Backlog);
    }

    [Fact]
    public async Task UpdateAsync_persists_title_and_description_and_touches_UpdatedAt()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("orig", "origdesc", null, repoId);
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

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);
        await mgr.TransitionToWorkQueueAsync(id);

        (await mgr.IsReadyAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task IsReady_false_when_dependency_not_merged()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", null, repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", null, repoId);
        await mgr.AddDependencyAsync(child, dep);

        (await mgr.IsReadyAsync(child)).Should().BeFalse();
    }

    [Fact]
    public async Task IsReady_true_after_dependency_marked_merged()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", null, repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", null, repoId);
        await mgr.AddDependencyAsync(child, dep);

        await mgr.LinkPullRequestAsync(dep, "https://forgejo/pr/1");
        await mgr.ManuallyMarkMergedAsync(dep);

        (await mgr.IsReadyAsync(child)).Should().BeTrue();
    }

    [Fact]
    public async Task AddDependency_rejects_self_loop()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);

        var act = async () => await mgr.AddDependencyAsync(id, id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddDependency_rejects_cycle()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var a = await mgr.CreateWorkItemAsync("a", "", null, repoId);
        var b = await mgr.CreateWorkItemAsync("b", "", null, repoId);
        var c = await mgr.CreateWorkItemAsync("c", "", null, repoId);

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

        var dep = await mgr.CreateWorkItemAsync("dep", "", null, repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", null, repoId);
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

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);

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

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);
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

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);
        await mgr.TransitionToReadyAsync(id);
        await mgr.TransitionToRunningAsync(id);

        var ok = await mgr.TransitionToHumanFeedbackAsync(id, "PR Awaiting Merge");

        ok.Should().BeTrue();
        var wi = await mgr.GetWorkItemAsync(id);
        wi!.Status.Should().Be(WorkItemStatus.HumanFeedback);
        wi.HumanFeedbackReason.Should().Be("PR Awaiting Merge");
    }

    [Fact]
    public async Task ManuallyMarkMerged_does_not_set_Done_when_active_LoopRun_exists()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);
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

        var id = await mgr.CreateWorkItemAsync("a", "", lt.Id, repoId);
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

        var id = await mgr.CreateWorkItemAsync("a", "", lt.Id, repoId);

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

        var id = await mgr.CreateWorkItemAsync("a", "", lt.Id, repoId);
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

        var id = await mgr.CreateWorkItemAsync("a", "", lt.Id, repoId);
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
    public async Task UpdateAsync_changes_loop_template_when_template_id_provided()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var ltA = new LoopTemplate { Id = Guid.NewGuid(), Name = "templateA" };
        db.Context.LoopTemplates.Add(ltA);
        var ltvA = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = ltA.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltvA);

        var ltB = new LoopTemplate { Id = Guid.NewGuid(), Name = "templateB" };
        db.Context.LoopTemplates.Add(ltB);
        var ltvB = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = ltB.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltvB);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", ltA.Id, repoId);

        var ok = await mgr.UpdateAsync(id, "a", "", ltB.Id);
        ok.Should().BeTrue();

        var reloaded = await mgr.GetWorkItemAsync(id);
        reloaded!.LoopTemplateVersionId.Should().Be(ltvB.Id);
    }

    [Fact]
    public async Task UpdateAsync_keeps_existing_template_when_null_provided()
    {
        var (mgr, db, repoId, _, _) = Setup();
        using var _ = db;

        var ltA = new LoopTemplate { Id = Guid.NewGuid(), Name = "templateA" };
        db.Context.LoopTemplates.Add(ltA);
        var ltvA = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = ltA.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltvA);
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("a", "", ltA.Id, repoId);

        var ok = await mgr.UpdateAsync(id, "a", "", null);
        ok.Should().BeTrue();

        var reloaded = await mgr.GetWorkItemAsync(id);
        reloaded!.LoopTemplateVersionId.Should().Be(ltvA.Id);
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

        var id = await mgr.CreateWorkItemAsync("a", "", lt.Id, repoId);
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
}
