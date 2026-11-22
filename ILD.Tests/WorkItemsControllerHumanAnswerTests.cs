using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>A stale answer is a 409 "the run moved on"; an empty one is not refused.</summary>
public class WorkItemsControllerHumanAnswerTests
{
    private static (WorkItemsController controller, Mock<IWorkItemManager> mgr, TestDb db) Build()
    {
        var db = new TestDb();
        var mgr = new Mock<IWorkItemManager>();
        var controller = TestActors.SignedIn(new WorkItemsController(
            mgr.Object,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorkItemPreview>().Object,
            new Mock<IRepositoryManager>().Object,
            db.LoopRuns,
            db.Providers,
            new Mock<IBranchNameOverrideService>().Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
            NullLogger<WorkItemsController>.Instance));
        return (controller, mgr, db);
    }

    private static string Error(IActionResult result, int status)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return obj.Value!.GetType().GetProperty("error")!.GetValue(obj.Value)!.ToString()!;
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task An_empty_Respond_is_handed_to_the_manager_on_both_routes_and_accepted(string blank)
    {
        var (controller, mgr, db) = Build();
        using var _ = db;
        var run = Guid.NewGuid();
        mgr.Setup(m => m.SubmitHumanFeedbackRespondAsync("w", run, blank, null)).ReturnsAsync(true);
        mgr.Setup(m => m.SubmitHumanFeedbackEdgeAsync("w", run, "Respond", blank, null)).ReturnsAsync(true);

        Assert.IsType<OkResult>(await controller.HumanFeedbackRespond("w", new HumanFeedbackInputRequest { RunId = run, Input = blank }));
        Assert.IsType<OkResult>(await controller.HumanFeedbackEdge("w", new HumanFeedbackEdgeRequest { RunId = run, Name = "Respond", Input = blank }));
    }

    [Fact]
    public async Task A_stale_answer_is_still_a_409()
    {
        var (controller, mgr, db) = Build();
        using var _ = db;
        mgr.Setup(m => m.SubmitHumanFeedbackRespondAsync("w", It.IsAny<Guid>(), "again", null))
            .ThrowsAsync(new HumanFeedbackRefusedException("The question this answer is for is no longer open."));

        var result = await controller.HumanFeedbackRespond("w", new HumanFeedbackInputRequest { RunId = Guid.NewGuid(), Input = "again" });

        Assert.Equal("The question this answer is for is no longer open.", Error(result, 409));
    }
}
