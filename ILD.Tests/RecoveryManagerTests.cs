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

        Assert.True(ok);
        engine.Verify(e => e.CancelRunAsync(runId), Times.Once);
    }

    [Fact]
    public async Task RecoverRunAsync_with_NeedsReview_marks_work_item_HumanFeedback()
    {
        var (mgr, wiMgr, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid().ToString();
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

        Assert.True(ok);
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

        Assert.True(ok);
        engine.Verify(e => e.ResumeRecoveredRunAsync(runId), Times.Once);
    }

    [Fact]
    public async Task RecoverRunAsync_with_AutoResume_and_unhealthy_worktree_marks_work_item_HumanFeedback()
    {
        var (mgr, wiMgr, runStore, _, _, repo, engine) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid().ToString();
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

        Assert.True(ok);
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

        Assert.False((await mgr.RecoverRunAsync(runId)));
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

        Assert.False((await mgr.RecoverRunAsync(runId)));
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ValidateWorktreeHealthAsync_returns_true_when_repo_validates_path()
    {
        var (mgr, wiMgr, runStore, _, _, repo, _) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid().ToString();
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

        Assert.True((await mgr.ValidateWorktreeHealthAsync(runId)));
    }

    [Fact]
    public async Task ValidateWorktreeHealthAsync_returns_false_when_repo_reports_corrupted()
    {
        var (mgr, wiMgr, runStore, _, _, repo, _) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid().ToString();
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

        Assert.False((await mgr.ValidateWorktreeHealthAsync(runId)));
    }

    [Fact]
    public async Task ValidateWorktreeHealthAsync_returns_false_when_no_worktree_path()
    {
        var (mgr, wiMgr, runStore, _, _, _, _) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid().ToString();
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

        Assert.False((await mgr.ValidateWorktreeHealthAsync(runId)));
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

        Assert.Equal(RecoveryPolicy.Cancel, (await mgr.GetRecoveryPolicyAsync(tid)));
    }

    [Fact]
    public async Task GetRecoveryPolicyAsync_defaults_to_AutoResume_when_template_missing()
    {
        var (mgr, _, _, _, tmpl, _, _) = Build();
        var tid = Guid.NewGuid();
        tmpl.Setup(s => s.GetByIdAsync(tid)).ReturnsAsync((LoopTemplate?)null);

        Assert.Equal(RecoveryPolicy.AutoResume, (await mgr.GetRecoveryPolicyAsync(tid)));
    }

    [Fact]
    public async Task RecoverRunAsync_with_AutoResume_and_WaitingHuman_node_does_not_resume()
    {
        var (mgr, wiMgr, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        var wiId = Guid.NewGuid().ToString();
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

        Assert.True(ok);
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
        engine.Verify(e => e.CancelRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    // ----- shutdown-halted runs -----

    /// <summary>
    /// A run the shutdown drain parked, as the store hands it back: WaitingHuman,
    /// halted, and stamped Shutdown. <paramref name="haltReason"/> is null for the
    /// other shape this file cares about — a halt a <i>human</i> pressed, which
    /// every row written before shutdown draining existed also looks like.
    /// </summary>
    private static LoopRun HaltedRun(Guid runId, string wiId, HaltReason? haltReason,
        RecoveryPolicy policy = RecoveryPolicy.AutoResume, string? worktreePath = null)
        => new()
        {
            Id = runId,
            WorkItemId = wiId,
            Status = LoopRunStatus.WaitingHuman,
            IsHalted = true,
            HaltReason = haltReason,
            RecoveryPolicy = policy,
            CurrentNodeId = Guid.NewGuid(),
            WorktreePath = worktreePath,
        };

    [Fact]
    public async Task RecoverRunAsync_resumes_a_shutdown_halted_run_through_the_halt_path()
    {
        // Not ResumeRecoveredRunAsync: that re-drives the AI node cold and throws
        // away the agent session the park exists to keep. A null note is what
        // makes the executor continue that session.
        var (mgr, _, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(HaltedRun(runId, "wi-1", HaltReason.Shutdown));

        Assert.True(await mgr.RecoverRunAsync(runId));

        engine.Verify(e => e.ResumeFromHaltAsync(runId, null), Times.Once);
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RecoverRunAsync_leaves_a_human_halted_run_completely_alone()
    {
        // The whole point of discriminating the two halts: a person is waiting to
        // steer this run, and a restart is not their cue to have it taken away.
        var (mgr, wiMgr, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(HaltedRun(runId, "wi-1", haltReason: null));

        Assert.False(await mgr.RecoverRunAsync(runId));

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
        engine.Verify(e => e.CancelRunAsync(It.IsAny<Guid>()), Times.Never);
        wiMgr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecoverRunAsync_leaves_a_throttle_parked_run_alone()
    {
        // A throttle park behaves like a human halt, NOT like a shutdown park:
        // auto-resuming it on startup is exactly the blanket auto-resume that was
        // considered and declined, and it would fire the run straight back into a
        // limit that has not reset yet.
        var (mgr, wiMgr, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(HaltedRun(runId, "wi-1", HaltReason.Throttled));

        Assert.False(await mgr.RecoverRunAsync(runId));

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        engine.Verify(e => e.ResumeRecoveredRunAsync(It.IsAny<Guid>()), Times.Never);
        engine.Verify(e => e.CancelRunAsync(It.IsAny<Guid>()), Times.Never);
        wiMgr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecoverRunAsync_honours_the_Cancel_policy_for_a_shutdown_halt()
    {
        // "The restart was a tidy one" is not grounds to overrule an operator's
        // explicit statement about what a restart should do to their runs.
        var (mgr, _, runStore, _, _, _, engine) = Build();
        var runId = Guid.NewGuid();
        runStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(HaltedRun(runId, "wi-1", HaltReason.Shutdown, RecoveryPolicy.Cancel));

        Assert.True(await mgr.RecoverRunAsync(runId));

        engine.Verify(e => e.CancelRunAsync(runId), Times.Once);
        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task RecoverRunAsync_still_checks_worktree_health_for_a_shutdown_halt()
    {
        var (mgr, wiMgr, runStore, _, _, repo, engine) = Build();
        var runId = Guid.NewGuid();
        const string worktreePath = "/tmp/wt";
        runStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(HaltedRun(runId, "wi-1", HaltReason.Shutdown, worktreePath: worktreePath));
        repo.Setup(r => r.ValidateWorktreeHealthAsync(worktreePath)).ReturnsAsync(false);

        Assert.True(await mgr.RecoverRunAsync(runId));

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        wiMgr.Verify(m => m.TransitionAsync("wi-1", RemoteWorkItemStatus.HumanFeedback,
            It.Is<string?>(reason => reason != null && reason.Contains(worktreePath, StringComparison.Ordinal)),
            It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task GetRecoverableRunIdsAsync_covers_crashed_and_shutdown_halted_runs_only()
    {
        // One query over the alive set, filtered to the two shapes startup can do
        // something about: a Running run left by a crash, and a run the drain
        // parked. A human halt, a throttle park, and a genuine Human/PR-node park
        // all stay out — each of those is waiting on a person, not on a restart.
        var (mgr, _, runStore, _, _, _, _) = Build();
        var crashed = new LoopRun { Id = Guid.NewGuid(), Status = LoopRunStatus.Running };
        var drained = HaltedRun(Guid.NewGuid(), "wi-2", HaltReason.Shutdown);
        var humanHalted = HaltedRun(Guid.NewGuid(), "wi-3", haltReason: null);
        var throttled = HaltedRun(Guid.NewGuid(), "wi-4", HaltReason.Throttled);
        var parked = new LoopRun { Id = Guid.NewGuid(), Status = LoopRunStatus.WaitingHuman };
        runStore.Setup(s => s.GetActiveRunsAsync())
            .ReturnsAsync(new List<LoopRun> { crashed, drained, humanHalted, throttled, parked });

        var ids = (await mgr.GetRecoverableRunIdsAsync()).ToList();

        Assert.Equal(new[] { crashed.Id, drained.Id }, ids);
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

        Assert.Equal(RecoveryPolicy.NeedsReview, template.RecoveryPolicy);
        tmpl.Verify(s => s.UpdateTemplateAsync(template), Times.Once);
    }
}
