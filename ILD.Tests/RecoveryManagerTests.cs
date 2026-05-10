using FluentAssertions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

public class RecoveryManagerTests
{
    private static (
        RecoveryManager mgr,
        Mock<IWorkItemManager> wiMgr,
        Mock<ILoopRunStore> runStore,
        Mock<IProviderStore> provStore,
        Mock<ILoopTemplateStore> tmplStore,
        Mock<IRepositoryManager> repo,
        Mock<ILoopEngine> engine
    ) Build()
    {
        var wi = new Mock<IWorkItemManager>();
        var rn = new Mock<ILoopRunStore>();
        var pr = new Mock<IProviderStore>();
        var ts = new Mock<ILoopTemplateStore>();
        var rp = new Mock<IRepositoryManager>();
        var en = new Mock<ILoopEngine>();
        return (new RecoveryManager(wi.Object, rn.Object, pr.Object, ts.Object, rp.Object, en.Object), wi, rn, pr, ts, rp, en);
    }

    [Fact]
    public async Task RecoverRunAsync_with_Cancel_policy_calls_CancelRunAsync()
    {
        var (mgr, _, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.Cancel,
        });

        var ok = await mgr.RecoverRunAsync(runId);

        ok.Should().BeTrue();
        engine.Verify(e => e.CancelRunAsync(runId), Times.Once);
    }

    [Fact]
    public async Task RecoverRunAsync_with_NeedsReview_marks_work_item_HumanFeedback()
    {
        var (mgr, wiMgr, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            WorkItemId = wiId,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.NeedsReview,
        });
        wiMgr.Setup(m => m.TransitionAsync(wiId, RemoteWorkItemStatus.HumanFeedback, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(true);

        var ok = await mgr.RecoverRunAsync(runId);

        ok.Should().BeTrue();
        wiMgr.Verify(m => m.TransitionAsync(wiId, RemoteWorkItemStatus.HumanFeedback, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Once);
        engine.Verify(e => e.CancelRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RecoverRunAsync_with_AutoResume_uses_engine_ResumeRecoveredRunAsync()
    {
        var (mgr, _, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        });

        var ok = await mgr.RecoverRunAsync(runId);

        ok.Should().BeTrue();
        engine.Verify(e => e.ResumeRecoveredRunAsync(runId), Times.Once);
    }

    [Fact]
    public async Task RecoverRunAsync_with_AutoResume_and_unhealthy_worktree_marks_work_item_HumanFeedback()
    {
        var (mgr, wiMgr, runStore, _, _, repo, engine) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid();
        const string worktreePath = "/tmp/wt";

        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            WorkItemId = wiId,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            WorktreePath = worktreePath,
        });
        repo.Setup(r => r.ValidateWorktreeHealthAsync(worktreePath)).ReturnsAsync(false);
        wiMgr.Setup(m => m.TransitionAsync(
                wiId,
                RemoteWorkItemStatus.HumanFeedback,
                It.Is<string?>(reason => reason != null && reason.Contains("worktree", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<string?>(),
                It.IsAny<Guid?>()))
            .ReturnsAsync(true);

        var ok = await mgr.RecoverRunAsync(runId);

        ok.Should().BeTrue();
        wiMgr.Verify(m => m.TransitionAsync(
            wiId,
            RemoteWorkItemStatus.HumanFeedback,
            It.Is<string?>(reason => reason != null && reason.Contains(worktreePath, StringComparison.Ordinal)),
            It.IsAny<string?>(),
            It.IsAny<Guid?>()), Times.Once);
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RecoverRunAsync_returns_false_when_run_missing()
    {
        var (mgr, _, runStore, _, _, _, _) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync((LoopRun?)null);

        (await mgr.RecoverRunAsync(runId)).Should().BeFalse();
    }

    [Fact]
    public async Task RecoverRunAsync_returns_false_when_run_is_not_Running()
    {
        var (mgr, _, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            Status = LoopRunStatus.Completed,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        });

        (await mgr.RecoverRunAsync(runId)).Should().BeFalse();
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ValidateWorktreeHealthAsync_returns_true_when_repo_validates_path()
    {
        var (mgr, wiMgr, runStore, _, _, repo, _) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            WorkItemId = wiId,
            Status = LoopRunStatus.Running,
            WorktreePath = "/tmp/wt",
        });
        wiMgr.Setup(s => s.GetWorkItemAsync(wiId)).ReturnsAsync(new WorkItemView
        {
            Id = wiId,
            Title = "t",
            Description = "d",
        });
        repo.Setup(r => r.ValidateWorktreeHealthAsync("/tmp/wt")).ReturnsAsync(true);

        (await mgr.ValidateWorktreeHealthAsync(runId)).Should().BeTrue();
    }

    [Fact]
    public async Task ValidateWorktreeHealthAsync_returns_false_when_repo_reports_corrupted()
    {
        var (mgr, wiMgr, runStore, _, _, repo, _) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            WorkItemId = wiId,
            Status = LoopRunStatus.Running,
            WorktreePath = "/tmp/wt",
        });
        wiMgr.Setup(s => s.GetWorkItemAsync(wiId)).ReturnsAsync(new WorkItemView
        {
            Id = wiId,
            Title = "t",
            Description = "d",
        });
        repo.Setup(r => r.ValidateWorktreeHealthAsync("/tmp/wt")).ReturnsAsync(false);

        (await mgr.ValidateWorktreeHealthAsync(runId)).Should().BeFalse();
    }

    [Fact]
    public async Task ValidateWorktreeHealthAsync_returns_false_when_no_worktree_path()
    {
        var (mgr, wiMgr, runStore, _, _, _, _) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            WorkItemId = wiId,
            Status = LoopRunStatus.Running,
            WorktreePath = null,
        });
        wiMgr.Setup(s => s.GetWorkItemAsync(wiId)).ReturnsAsync(new WorkItemView
        {
            Id = wiId,
            Title = "t",
            Description = "d",
        });

        (await mgr.ValidateWorktreeHealthAsync(runId)).Should().BeFalse();
    }

    [Fact]
    public async Task GetRecoveryPolicyAsync_returns_template_policy_when_template_exists()
    {
        var (mgr, _, _, _, tmpl, _, _) = Build();
        var tid = Guid.NewGuid();
        tmpl.Setup(s => s.GetByIdAsync(tid)).ReturnsAsync(new LoopTemplate
        {
            Id = tid,
            Name = "n",
            Description = "d",
            RecoveryPolicy = RecoveryPolicy.Cancel,
        });

        (await mgr.GetRecoveryPolicyAsync(tid)).Should().Be(RecoveryPolicy.Cancel);
    }

    [Fact]
    public async Task GetRecoveryPolicyAsync_defaults_to_AutoResume_when_template_missing()
    {
        var (mgr, _, _, _, tmpl, _, _) = Build();
        var tid = Guid.NewGuid();
        tmpl.Setup(s => s.GetByIdAsync(tid)).ReturnsAsync((LoopTemplate?)null);

        (await mgr.GetRecoveryPolicyAsync(tid)).Should().Be(RecoveryPolicy.AutoResume);
    }

    [Fact]
    public async Task RecoverRunAsync_with_AutoResume_and_WaitingHuman_node_does_not_resume()
    {
        var (mgr, wiMgr, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid();
        var currentNodeId = Guid.NewGuid();
        var prRunNodeId = Guid.NewGuid();

        runStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            WorkItemId = wiId,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            CurrentNodeId = currentNodeId,
        });
        runStore.Setup(s => s.GetRunNodeAsync(runId, currentNodeId)).ReturnsAsync(new LoopRunNode
        {
            Id = prRunNodeId,
            LoopRunId = runId,
            LoopNodeId = currentNodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        });
        wiMgr.Setup(s => s.GetWorkItemAsync(wiId)).ReturnsAsync(new WorkItemView { Id = wiId, Title = "t", Description = "d", Status = RemoteWorkItemStatus.HumanFeedback });

        var ok = await mgr.RecoverRunAsync(runId);

        ok.Should().BeTrue();
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
        engine.Verify(e => e.CancelRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SetRecoveryPolicyAsync_persists_policy_via_template_store()
    {
        var (mgr, _, _, _, tmpl, _, _) = Build();
        var tid = Guid.NewGuid();
        var template = new LoopTemplate
        {
            Id = tid,
            Name = "n",
            Description = "d",
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        tmpl.Setup(s => s.GetByIdAsync(tid)).ReturnsAsync(template);

        await mgr.SetRecoveryPolicyAsync(tid, RecoveryPolicy.NeedsReview);

        template.RecoveryPolicy.Should().Be(RecoveryPolicy.NeedsReview);
        tmpl.Verify(s => s.UpdateTemplateAsync(template), Times.Once);
    }
}
