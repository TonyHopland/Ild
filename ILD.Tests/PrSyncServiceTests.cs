using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

public class PrSyncServiceTests
{
    [Fact]
    public async Task HandleWebhookAsync_signals_success_and_marks_merged_for_merged_payloads()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            PrUrl = "https://github.com/team/repo/pull/7",
            CurrentNodeId = Guid.NewGuid(),
        };
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = run.CurrentNodeId!.Value,
            Status = LoopRunNodeStatus.WaitingHuman,
        };

        var loopRuns = new Mock<ILoopRunStore>();
        loopRuns.Setup(s => s.GetByPrUrlAsync(run.PrUrl!)).ReturnsAsync(run);
        loopRuns.Setup(s => s.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value)).ReturnsAsync(runNode);

        var events = new Mock<IEventLogStore>();
        var workItems = new Mock<IWorkItemManager>();
        var engine = new Mock<ILoopEngine>();

        var service = new PrSyncService(loopRuns.Object, events.Object, workItems.Object, engine.Object);

        await service.HandleWebhookAsync(new WebhookPayload("pull_request.merged", "repo-1", "7", run.PrUrl, null, "merged"));

        loopRuns.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.IsPrMerged)), Times.Once);
        // Merge bookkeeping stays on the run matched by PR URL — going through
        // the work item would tag whatever run is "current" (wrong run once
        // each run has its own PR, ADR-0008).
        workItems.Verify(s => s.ManuallyMarkMergedAsync(It.IsAny<string>()), Times.Never);
        engine.Verify(s => s.SignalNodeResultAsync(run.Id, runNode.Id, It.Is<NodeSignal>(signal => signal.Type == ExternalActionResultType.Success)), Times.Once);
    }

    [Fact]
    public async Task HandleWebhookAsync_merge_of_stale_runs_pr_does_not_touch_workitem_or_current_run()
    {
        // Re-running a work item opens a new PR per run; the old run's PR stays
        // open. Merging it must not mark the current run merged or finish the
        // work item out from under the active run.
        var staleRun = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            PrUrl = "https://github.com/team/repo/pull/7",
            Status = LoopRunStatus.Failed,
        };
        var currentRun = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "wi-1", Status = LoopRunStatus.Running };

        var loopRuns = new Mock<ILoopRunStore>();
        loopRuns.Setup(s => s.GetByPrUrlAsync(staleRun.PrUrl!)).ReturnsAsync(staleRun);
        loopRuns.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(currentRun);
        loopRuns.Setup(s => s.GetRunNodesAsync(staleRun.Id)).ReturnsAsync(Array.Empty<LoopRunNode>());

        var workItems = new Mock<IWorkItemManager>();
        var service = new PrSyncService(loopRuns.Object, new Mock<IEventLogStore>().Object, workItems.Object, new Mock<ILoopEngine>().Object);

        await service.HandleWebhookAsync(new WebhookPayload("pull_request.merged", "repo-1", "7", staleRun.PrUrl, null, "merged"));

        loopRuns.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.Id == staleRun.Id && r.IsPrMerged)), Times.Once);
        loopRuns.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.Id == currentRun.Id)), Times.Never);
        workItems.Verify(s => s.ManuallyMarkMergedAsync(It.IsAny<string>()), Times.Never);
        workItems.Verify(s => s.TransitionAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        Assert.False(currentRun.IsPrMerged);
    }

    [Fact]
    public async Task HandleWebhookAsync_merge_of_terminal_current_runs_pr_finishes_workitem()
    {
        // Human merged the PR despite the run having failed: with no engine
        // path left to resume, the merge finishes the work item directly.
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            PrUrl = "https://github.com/team/repo/pull/7",
            Status = LoopRunStatus.Failed,
        };

        var loopRuns = new Mock<ILoopRunStore>();
        loopRuns.Setup(s => s.GetByPrUrlAsync(run.PrUrl!)).ReturnsAsync(run);
        loopRuns.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(run);
        loopRuns.Setup(s => s.GetRunNodesAsync(run.Id)).ReturnsAsync(Array.Empty<LoopRunNode>());

        var workItems = new Mock<IWorkItemManager>();
        var service = new PrSyncService(loopRuns.Object, new Mock<IEventLogStore>().Object, workItems.Object, new Mock<ILoopEngine>().Object);

        await service.HandleWebhookAsync(new WebhookPayload("pull_request.merged", "repo-1", "7", run.PrUrl, null, "merged"));

        loopRuns.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.IsPrMerged)), Times.Once);
        workItems.Verify(s => s.TransitionAsync("wi-1", RemoteWorkItemStatus.Done,
            It.IsAny<string?>(), It.IsAny<string?>(), run.Id, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task HandleWebhookAsync_signals_failure_for_rejected_payloads()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            PrUrl = "https://github.com/team/repo/pull/7",
            CurrentNodeId = Guid.NewGuid(),
        };
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = run.CurrentNodeId!.Value,
            Status = LoopRunNodeStatus.WaitingHuman,
        };

        var loopRuns = new Mock<ILoopRunStore>();
        loopRuns.Setup(s => s.GetByPrUrlAsync(run.PrUrl!)).ReturnsAsync(run);
        loopRuns.Setup(s => s.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value)).ReturnsAsync(runNode);

        var events = new Mock<IEventLogStore>();
        var workItems = new Mock<IWorkItemManager>();
        var engine = new Mock<ILoopEngine>();

        var service = new PrSyncService(loopRuns.Object, events.Object, workItems.Object, engine.Object);

        await service.HandleWebhookAsync(new WebhookPayload("pull_request.rejected", "repo-1", "7", run.PrUrl, "needs work", "changes_requested"));

        events.Verify(s => s.AppendAsync(It.Is<EventLog>(e => e.Data == "needs work")), Times.Once);
        workItems.Verify(s => s.ManuallyMarkMergedAsync(It.IsAny<string>()), Times.Never);
        engine.Verify(s => s.SignalNodeResultAsync(run.Id, runNode.Id, It.Is<NodeSignal>(signal => signal.Type == ExternalActionResultType.Reject && signal.Error == "needs work")), Times.Once);
    }
}