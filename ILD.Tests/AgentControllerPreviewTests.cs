using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The agent's preview routes are glue over <see cref="IWorkItemPreview"/> (see <see cref="WorkItemPreviewTests"/>):
/// the reach check first, then the module, then the caller's address on the preview a route answers with.
/// </summary>
public class AgentControllerPreviewTests
{
    private const string WorktreePath = "/tmp/worktrees/agent-preview-wi";

    private static AgentController BuildController(Mock<IWorkItemPreview> preview, TestDb db, System.Security.Claims.ClaimsPrincipal? user = null)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync((string id) => new WorkItemView { Id = id, WorktreePath = WorktreePath });
        var controller = new AgentController(
            workItems.Object,
            new Mock<ILoopTemplateManager>().Object,
            db.LoopRuns,
            db.Context,
            preview.Object,
            new Mock<IChatLoopScratchpad>().Object,
            new Mock<IChatNotifier>().Object,
            AgentSessions.Resolver(db, workItems.Object));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user ?? new System.Security.Claims.ClaimsPrincipal() },
        };
        return controller;
    }

    private static WorktreePreviewResponse Running() => new()
    {
        State = "running",
        WorktreePath = WorktreePath,
        Services =
        {
            new WorktreePreviewServiceResponse
            {
                Name = "web",
                Port = 5173,
                PublicUrl = "http://wi-7.ild.localhost:8080",
                LocalUrl = "http://127.0.0.1:5173",
            },
        },
    };

    private static PreviewResult<WorktreePreviewResponse> Answer() => PreviewResult<WorktreePreviewResponse>.Ok(Running());

    [Fact]
    public async Task Every_route_hands_its_arguments_to_the_module_and_answers_with_what_it_returns()
    {
        using var db = new TestDb();
        var preview = new Mock<IWorkItemPreview>();
        var request = new WorktreePreviewStartRequest { StartDependencies = true };
        var update = new WorktreePreviewServiceConfigUpdateRequest { Config = "{}" };
        preview.Setup(p => p.GetStatusAsync("w")).ReturnsAsync(Answer());
        preview.Setup(p => p.StartAsync("w", request)).ReturnsAsync(Answer());
        preview.Setup(p => p.StopAsync("w")).ReturnsAsync(Answer());
        preview.Setup(p => p.StartServiceAsync("w", "web", request, false)).ReturnsAsync(Answer());
        preview.Setup(p => p.StopServiceAsync("w", "web")).ReturnsAsync(Answer());
        preview.Setup(p => p.GetServiceConfigAsync("w", "web"))
            .ReturnsAsync(PreviewResult<WorktreePreviewServiceConfigResponse>.Ok(new() { Service = "web", Config = "{}" }));
        preview.Setup(p => p.UpdateServiceConfigAsync("w", "web", update))
            .ReturnsAsync(PreviewResult<WorktreePreviewServiceConfigResponse>.Failed(PreviewRefusal.Refused, "no"));
        preview.Setup(p => p.GetLogAsync("w", "web"))
            .ReturnsAsync(PreviewResult<WorktreePreviewLogResponse>.Failed(PreviewRefusal.WorkItemNotFound));
        var controller = BuildController(preview, db);

        Assert.IsType<OkObjectResult>(await controller.GetPreview("w"));
        Assert.IsType<OkObjectResult>(await controller.StartPreview("w", request));
        Assert.IsType<OkObjectResult>(await controller.StopPreview("w"));
        // The agent has no start-dependencies option: whatever the request says, the module is told false.
        Assert.IsType<OkObjectResult>(await controller.StartPreviewService("w", "web", request));
        Assert.IsType<OkObjectResult>(await controller.StopPreviewService("w", "web"));
        Assert.IsType<OkObjectResult>(await controller.GetPreviewServiceConfig("w", "web"));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdatePreviewServiceConfig("w", "web", update));
        Assert.IsType<NotFoundResult>(await controller.GetPreviewLog("w", "web"));
    }

    private static async Task<WorktreePreviewServiceResponse> ServiceOf(IActionResult result)
        => Assert.Single(Assert.IsType<WorktreePreviewResponse>(Assert.IsType<OkObjectResult>(result).Value).Services);

    private static async Task<WorktreePreviewServiceResponse> GetAgentService(
        string workItemId, System.Security.Claims.ClaimsPrincipal? user, TestDb db)
    {
        var preview = new Mock<IWorkItemPreview>();
        preview.Setup(p => p.GetStatusAsync(workItemId)).ReturnsAsync(Answer());
        return await ServiceOf(await BuildController(preview, db, user).GetPreview(workItemId));
    }

    [Fact]
    public async Task The_callers_address_is_applied_to_every_route_that_answers_with_a_preview()
    {
        using var db = new TestDb();
        var preview = new Mock<IWorkItemPreview>();
        preview.Setup(p => p.StartAsync("w", null)).ReturnsAsync(Answer());
        preview.Setup(p => p.StopAsync("w")).ReturnsAsync(Answer());
        preview.Setup(p => p.StartServiceAsync("w", "web", null, false)).ReturnsAsync(Answer());
        preview.Setup(p => p.StopServiceAsync("w", "web")).ReturnsAsync(Answer());
        var controller = BuildController(preview, db);

        foreach (var result in new[]
        {
            await controller.StartPreview("w", null),
            await controller.StopPreview("w"),
            await controller.StartPreviewService("w", "web", null),
            await controller.StopPreviewService("w", "web"),
        })
            Assert.Equal("http://wi-7.ild.localhost:8080", (await ServiceOf(result)).AgentUrl);
    }

    private static Guid SeedRun(TestDb db, string workItemId)
    {
        var template = new ILD.Data.Entities.LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new ILD.Data.Entities.LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new ILD.Data.Entities.LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = ILD.Data.Enums.LoopRunStatus.Running,
            RecoveryPolicy = ILD.Data.Enums.RecoveryPolicy.AutoResume,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run.Id;
    }


    [Fact]
    public async Task A_run_on_the_same_work_item_gets_the_local_address_as_agentUrl()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var runId = SeedRun(db, id);
        var run = ILD.Tests.Integration.AgentTokenTestExtensions.AgentPrincipal(ILD.Data.Entities.AgentTokenSource.Run, runId);

        var service = await GetAgentService(id, run, db);

        Assert.Equal("http://127.0.0.1:5173", service.AgentUrl);
        Assert.Equal("http://wi-7.ild.localhost:8080", service.PublicUrl);
    }

    [Fact]
    public async Task A_run_on_another_work_item_is_refused_before_the_module_is_called()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var runId = SeedRun(db, Guid.NewGuid().ToString());
        var preview = new Mock<IWorkItemPreview>();
        var controller = BuildController(
            preview, db, ILD.Tests.Integration.AgentTokenTestExtensions.AgentPrincipal(ILD.Data.Entities.AgentTokenSource.Run, runId));

        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetPreview(id)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.StartPreview(id, null)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.StopPreview(id)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.StartPreviewService(id, "web", null)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.StopPreviewService(id, "web")).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetPreviewServiceConfig(id, "web")).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.UpdatePreviewServiceConfig(id, "web", null)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetPreviewLog(id, "web")).StatusCode);
        Assert.Empty(preview.Invocations);
    }

    [Fact]
    public async Task A_chat_session_gets_the_public_address_as_agentUrl()
    {
        using var db = new TestDb();
        var chat = ILD.Tests.Integration.AgentTokenTestExtensions.AgentPrincipal(ILD.Data.Entities.AgentTokenSource.Chat, Guid.NewGuid());

        var service = await GetAgentService(Guid.NewGuid().ToString(), chat, db);

        Assert.Equal("http://wi-7.ild.localhost:8080", service.AgentUrl);
    }

    [Fact]
    public async Task A_call_without_headers_gets_the_public_address_as_agentUrl()
    {
        using var db = new TestDb();
        var service = await GetAgentService(Guid.NewGuid().ToString(), null, db);

        Assert.Equal("http://wi-7.ild.localhost:8080", service.AgentUrl);
    }
}
