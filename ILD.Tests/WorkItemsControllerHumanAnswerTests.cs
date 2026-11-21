using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>A blank text answer is a 400 the person can act on; a stale one stays the 409 "the run moved on".</summary>
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

    [Fact]
    public async Task A_blank_Respond_is_a_400_with_a_message_on_the_respond_route()
    {
        var (controller, mgr, db) = Build();
        using var _ = db;
        mgr.Setup(m => m.SubmitHumanFeedbackRespondAsync("w", It.IsAny<Guid>(), "  ", null)).ThrowsAsync(new BlankHumanAnswerException());

        var result = await controller.HumanFeedbackRespond("w", new HumanFeedbackInputRequest { RunId = Guid.NewGuid(), Input = "  " });

        Assert.Equal("Type an answer before sending.", Error(result, 400));
    }

    [Fact]
    public async Task A_blank_Respond_is_a_400_on_the_edge_route_too()
    {
        var (controller, mgr, db) = Build();
        using var _ = db;
        mgr.Setup(m => m.SubmitHumanFeedbackEdgeAsync("w", It.IsAny<Guid>(), "Respond", "", null)).ThrowsAsync(new BlankHumanAnswerException());

        var result = await controller.HumanFeedbackEdge("w", new HumanFeedbackEdgeRequest { RunId = Guid.NewGuid(), Name = "Respond", Input = "" });

        Assert.Equal("Type an answer before sending.", Error(result, 400));
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
