using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The UI's preview routes are glue over <see cref="IWorkItemPreview"/> (see <see cref="WorkItemPreviewTests"/>):
/// they hand it their arguments and answer with what it returns.
/// </summary>
public class WorkItemsControllerPreviewTests
{
    private static PreviewResult<WorktreePreviewResponse> Answer() => PreviewResult<WorktreePreviewResponse>.Ok(new() { State = "running" });

    private static WorkItemsController Build(Mock<IWorkItemPreview> preview, TestDb db) => TestActors.SignedIn(new WorkItemsController(
        new Mock<IWorkItemManager>().Object,
        new Mock<ILoopEngine>().Object,
        preview.Object,
        new Mock<IRepositoryManager>().Object,
        db.LoopRuns,
        db.Providers,
        new Mock<IBranchNameOverrideService>().Object,
        ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
        NullLogger<WorkItemsController>.Instance));

    [Fact]
    public async Task Every_route_hands_its_arguments_to_the_module_and_answers_with_what_it_returns()
    {
        using var db = new TestDb();
        var preview = new Mock<IWorkItemPreview>();
        var request = new WorktreePreviewStartRequest();
        var update = new WorktreePreviewServiceConfigUpdateRequest { Config = "{}" };
        preview.Setup(p => p.GetStatusAsync("w")).ReturnsAsync(Answer());
        preview.Setup(p => p.StartAsync("w", request)).ReturnsAsync(Answer());
        preview.Setup(p => p.StopAsync("w")).ReturnsAsync(Answer());
        preview.Setup(p => p.StopServiceAsync("w", "web")).ReturnsAsync(Answer());
        preview.Setup(p => p.GetServiceConfigAsync("w", "web"))
            .ReturnsAsync(PreviewResult<WorktreePreviewServiceConfigResponse>.Failed(PreviewRefusal.ConfigNotFound, "none"));
        preview.Setup(p => p.UpdateServiceConfigAsync("w", "web", update))
            .ReturnsAsync(PreviewResult<WorktreePreviewServiceConfigResponse>.Failed(PreviewRefusal.Refused, "no"));
        preview.Setup(p => p.GetLogAsync("w", "web"))
            .ReturnsAsync(PreviewResult<WorktreePreviewLogResponse>.Failed(PreviewRefusal.WorkItemNotFound));
        var controller = Build(preview, db);

        Assert.IsType<OkObjectResult>(await controller.GetPreview("w"));
        Assert.IsType<OkObjectResult>(await controller.StartPreview("w", request));
        Assert.IsType<OkObjectResult>(await controller.StopPreview("w"));
        Assert.IsType<OkObjectResult>(await controller.StopPreviewService("w", "web"));
        Assert.IsType<NotFoundObjectResult>(await controller.GetPreviewServiceConfig("w", "web"));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdatePreviewServiceConfig("w", "web", update));
        Assert.IsType<NotFoundResult>(await controller.GetPreviewLog("w", "web"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_service_start_passes_the_requests_start_dependencies_choice(bool? requested, bool expected)
    {
        using var db = new TestDb();
        var preview = new Mock<IWorkItemPreview>();
        var request = requested is { } r ? new WorktreePreviewStartRequest { StartDependencies = r } : null;
        preview.Setup(p => p.StartServiceAsync("w", "web", request, expected)).ReturnsAsync(Answer());

        Assert.IsType<OkObjectResult>(await Build(preview, db).StartPreviewService("w", "web", request));
    }
}
