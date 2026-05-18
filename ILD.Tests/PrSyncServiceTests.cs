using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
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
        workItems.Verify(s => s.ManuallyMarkMergedAsync(run.WorkItemId), Times.Once);
        engine.Verify(s => s.SignalNodeResultAsync(run.Id, runNode.Id, It.Is<NodeSignal>(signal => signal.Success)), Times.Once);
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
        engine.Verify(s => s.SignalNodeResultAsync(run.Id, runNode.Id, It.Is<NodeSignal>(signal => !signal.Success && signal.Error == "needs work")), Times.Once);
    }
}