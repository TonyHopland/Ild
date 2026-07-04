using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The agent-facing preview endpoints must broadcast PreviewStateChanged just like
/// the human WorkItemsController, so an open Preview tab live-updates when an AI
/// agent starts or stops the preview through the agent API. These tests pin that
/// contract (and that read-only / rejected calls stay silent).
/// </summary>
public class AgentControllerPreviewTests
{
    private const string WorktreePath = "/tmp/worktrees/agent-preview-wi";

    private static AgentController BuildController(
        WorkItemView? workItem,
        Mock<IWorktreePreviewService> preview,
        Mock<IWorkItemNotifier> notifier,
        TestDb db)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);

        return new AgentController(
            workItems.Object,
            new Mock<ILoopTemplateManager>().Object,
            db.LoopRuns,
            db.Context,
            preview.Object,
            new Mock<IChatLoopScratchpad>().Object,
            new Mock<IChatNotifier>().Object,
            notifier.Object);
    }

    private static WorkItemView ItemWithWorktree(string id) => new()
    {
        Id = id,
        WorktreePath = WorktreePath,
    };

    [Fact]
    public async Task StartPreview_broadcasts_preview_state_changed()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = WorktreePath });
        var notifier = new Mock<IWorkItemNotifier>();
        var controller = BuildController(ItemWithWorktree(id), preview, notifier, db);

        var result = await controller.StartPreview(id, new WorktreePreviewStartRequest());

        Assert.IsType<OkObjectResult>(result);
        notifier.Verify(n => n.PreviewStateChangedAsync(id), Times.Once);
    }

    [Fact]
    public async Task StopPreview_broadcasts_preview_state_changed()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StopAsync(WorktreePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "stopped", WorktreePath = WorktreePath });
        var notifier = new Mock<IWorkItemNotifier>();
        var controller = BuildController(ItemWithWorktree(id), preview, notifier, db);

        var result = await controller.StopPreview(id);

        Assert.IsType<OkObjectResult>(result);
        notifier.Verify(n => n.PreviewStateChangedAsync(id), Times.Once);
    }

    [Fact]
    public async Task StartPreviewService_broadcasts_preview_state_changed()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartServiceAsync(WorktreePath, "web", It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = WorktreePath });
        var notifier = new Mock<IWorkItemNotifier>();
        var controller = BuildController(ItemWithWorktree(id), preview, notifier, db);

        var result = await controller.StartPreviewService(id, "web", new WorktreePreviewStartRequest());

        Assert.IsType<OkObjectResult>(result);
        notifier.Verify(n => n.PreviewStateChangedAsync(id), Times.Once);
    }

    [Fact]
    public async Task StopPreviewService_broadcasts_preview_state_changed()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StopServiceAsync(WorktreePath, "web", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "stopped", WorktreePath = WorktreePath });
        var notifier = new Mock<IWorkItemNotifier>();
        var controller = BuildController(ItemWithWorktree(id), preview, notifier, db);

        var result = await controller.StopPreviewService(id, "web");

        Assert.IsType<OkObjectResult>(result);
        notifier.Verify(n => n.PreviewStateChangedAsync(id), Times.Once);
    }

    [Fact]
    public async Task GetPreview_does_not_broadcast()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.GetStatusAsync(WorktreePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "stopped", WorktreePath = WorktreePath });
        var notifier = new Mock<IWorkItemNotifier>();
        var controller = BuildController(ItemWithWorktree(id), preview, notifier, db);

        var result = await controller.GetPreview(id);

        Assert.IsType<OkObjectResult>(result);
        // Reading status must not tell every board client the preview changed.
        notifier.Verify(n => n.PreviewStateChangedAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StartPreview_for_item_without_worktree_does_not_broadcast()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var preview = new Mock<IWorktreePreviewService>();
        var notifier = new Mock<IWorkItemNotifier>();
        // No worktree => the preview surface refuses with 400 before doing anything,
        // so nothing changed and no broadcast should fire.
        var controller = BuildController(new WorkItemView { Id = id, WorktreePath = null }, preview, notifier, db);

        var result = await controller.StartPreview(id, new WorktreePreviewStartRequest());

        Assert.IsType<BadRequestObjectResult>(result);
        preview.Verify(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
        notifier.Verify(n => n.PreviewStateChangedAsync(It.IsAny<string>()), Times.Never);
    }
}
