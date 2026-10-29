using System.Runtime.Versioning;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The start request's <c>wait</c> option: the user and agent endpoints send only an explicit
/// <c>wait: false</c> to the background start, and that start still refuses a missing config
/// or an unknown profile at once instead of failing later in the background.
/// </summary>
[SupportedOSPlatform("linux")]
public class PreviewStartWaitTests
{
    private const string WorktreePath = "/tmp/worktrees/preview-wait-wi";

    private static readonly WorktreePreviewResponse Answer = new() { State = "starting", WorktreePath = WorktreePath };

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, null)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Only_an_explicit_wait_false_begins_the_start_in_the_background(bool agent, bool? wait)
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Answer);
        preview.Setup(p => p.BeginStartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Answer);
        var request = wait is { } w ? new WorktreePreviewStartRequest { Wait = w } : null;

        var result = agent
            ? await BuildAgent(id, preview, db).StartPreview(id, request)
            : await BuildUser(id, preview, db).StartPreview(id, request);

        Assert.IsType<OkObjectResult>(result);
        var background = wait == false;
        preview.Verify(p => p.BeginStartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), background ? Times.Once : Times.Never);
        preview.Verify(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), background ? Times.Never : Times.Once);
    }

    [Fact]
    public async Task A_background_start_of_a_missing_config_or_unknown_profile_is_refused_at_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "ild-start-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var service = new WorktreePreviewService(
                new Mock<IHttpClientFactory>().Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
                NullLogger<WorktreePreviewService>.Instance, agentUser: null, agentGroup: null, agentHome: null);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.BeginStartAsync(root, null, TestContext.Current.CancellationToken));

            File.WriteAllText(Path.Combine(root, "ild.config.json"),
                """{"preview":{"defaultProfile":"app","profiles":{"app":{"services":[{"name":"web","port":"web","command":"exit 0","healthUrl":"http://127.0.0.1:${PORT}/"}]}}}}""");
            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.BeginStartAsync(root, new WorktreePreviewStartOptions(ProfileName: "nope"), TestContext.Current.CancellationToken));
            Assert.Contains("nope", unknown.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentController BuildAgent(string id, Mock<IWorktreePreviewService> preview, TestDb db)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(id)).ReturnsAsync(new WorkItemView { Id = id, WorktreePath = WorktreePath });
        return new AgentController(
            workItems.Object,
            new Mock<ILoopTemplateManager>().Object,
            db.LoopRuns,
            db.Context,
            db.Providers,
            NoPackageFeeds.Resolver,
            preview.Object,
            new Mock<IChatLoopScratchpad>().Object,
            new Mock<IChatNotifier>().Object,
            TestActors.Instance,
            new Mock<IWorkItemNotifier>().Object);
    }

    private static WorkItemsController BuildUser(string id, Mock<IWorktreePreviewService> preview, TestDb db)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(id)).ReturnsAsync(new WorkItemView { Id = id, WorktreePath = WorktreePath });
        return TestActors.SignedIn(new WorkItemsController(
            workItems.Object,
            new Mock<ILoopEngine>().Object,
            preview.Object,
            new Mock<IRepositoryManager>().Object,
            db.LoopRuns,
            db.Providers,
            NoPackageFeeds.Resolver,
            new Mock<IBranchNameOverrideService>().Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
            NullLogger<WorkItemsController>.Instance,
            new Mock<IWorkItemNotifier>().Object));
    }
}
