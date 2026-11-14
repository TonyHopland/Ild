using ILD.Api.Controllers;
using ILD.Api.Services;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>The API for a node that is Waiting for ILD: Restart and Resume, each with optional input, and what the run detail shows.</summary>
public class LoopRunsControllerWaitingForIldTests
{
    private static LoopRunsController Controller(Mock<ILoopEngine> engine, LoopRun? run = null)
    {
        var store = new Mock<ILoopRunStore>();
        if (run is not null) store.Setup(s => s.GetByIdAsync(run.Id)).ReturnsAsync(run);
        var controller = TestActors.SignedIn(new LoopRunsController(engine.Object, new Mock<IEventLogService>().Object, store.Object,
            new Mock<IAdapterSessionSnapshotStore>().Object,
            new InteractiveShellSessionService(NullLogger<InteractiveShellSessionService>.Instance, host: LocalRunHost.Shared),
            new Mock<IRunReclaimer>().Object, new Mock<IWorkItemManager>().Object, NoPackageFeeds.Resolver, new Mock<IRunConversationService>().Object,
            Mock.Of<ILoopTemplateStore>(), Mock.Of<IGlobalVariableStore>(), Mock.Of<ISchedulerSettingsService>(), claudeProjects: TestClaudeProjectFolders.Instance));
        controller.HttpContext.RequestServices = new ServiceCollection().AddSingleton<IRunHost>(new FakeRunHost()).BuildServiceProvider();
        return controller;
    }

    [Fact]
    public async Task Restart_hands_the_signed_in_user_and_the_input_to_the_engine()
    {
        var engine = new Mock<ILoopEngine>();
        var id = Guid.NewGuid();

        var result = await Controller(engine).RestartWaitingForChildren(id.ToString(), new LoopRunsController.ResumeSteerRequest { Note = "Run only the unit tests." });

        Assert.IsType<OkResult>(result);
        engine.Verify(e => e.RestartWaitingForChildrenAsync(id, "Run only the unit tests.", "tester"), Times.Once);
        engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Resume_hands_the_signed_in_user_to_the_engine_and_the_input_is_optional()
    {
        var engine = new Mock<ILoopEngine>();
        var id = Guid.NewGuid();

        var result = await Controller(engine).ResumeWaitingForChildren(id.ToString(), null);

        Assert.IsType<OkResult>(result);
        engine.Verify(e => e.ResumeWaitingForChildrenAsync(id, null, "tester"), Times.Once);
        engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_run_that_is_not_waiting_is_a_conflict_not_a_server_error()
    {
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.RestartWaitingForChildrenAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("The run is not waiting"));

        var result = await Controller(engine).RestartWaitingForChildren(Guid.NewGuid().ToString(), null);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task A_malformed_id_is_a_bad_request()
    {
        var engine = new Mock<ILoopEngine>();

        Assert.IsType<BadRequestObjectResult>(await Controller(engine).ResumeWaitingForChildren("nope", null));
        engine.VerifyNoOtherCalls();
    }
}
