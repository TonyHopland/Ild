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

        var controller = new AgentController(
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
            notifier.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        return controller;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartPreview_threads_the_repository_custom_env_into_start_options(bool itemRepointed)
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();

        // Seed a repository carrying a custom .env, and point the work item at it.
        var repoId = Guid.NewGuid();
        var provider = new ILD.Data.Entities.RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "https://e" };
        db.Context.RemoteProviders.Add(provider);
        db.Context.Repositories.Add(new ILD.Data.Entities.Repository
        {
            Id = repoId,
            Name = "r",
            CloneUrl = "https://e/r.git",
            RemoteProviderId = provider.Id,
            PreviewEnv = "API_TOKEN=from-repo",
        });
        // Re-pointing the item since must not reach the run it already has.
        var editedRepoId = Guid.NewGuid();
        db.Context.Repositories.Add(new ILD.Data.Entities.Repository
        {
            Id = editedRepoId,
            Name = "b",
            CloneUrl = "https://e/b.git",
            RemoteProviderId = provider.Id,
            PreviewEnv = "API_TOKEN=from-edited-repo",
        });
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        WorktreePreviewStartOptions? captured = null;
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = WorktreePath });
        var notifier = new Mock<IWorkItemNotifier>();
        var workItem = itemRepointed
            ? new WorkItemView { Id = id, WorktreePath = WorktreePath, RepositoryId = editedRepoId, RunRepositoryId = repoId }
            : new WorkItemView { Id = id, WorktreePath = WorktreePath, RepositoryId = repoId };
        var controller = BuildController(workItem, preview, notifier, db);

        var result = await controller.StartPreview(id, new WorktreePreviewStartRequest());

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(captured);
        Assert.Equal("API_TOKEN=from-repo", captured!.CustomEnv);
    }

    [Fact]
    public async Task Preview_starts_carry_the_volume_scope_of_the_runs_repository_or_none_without_one()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var provider = new ILD.Data.Entities.RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "https://e" };
        db.Context.RemoteProviders.Add(provider);
        var runRepoId = Guid.NewGuid();
        var editedRepoId = Guid.NewGuid();
        db.Context.Repositories.Add(new ILD.Data.Entities.Repository
        {
            Id = runRepoId, Name = "r", CloneUrl = "https://e/r.git", RemoteProviderId = provider.Id, KeepPreviewData = true,
        });
        db.Context.Repositories.Add(new ILD.Data.Entities.Repository
        {
            Id = editedRepoId, Name = "b", CloneUrl = "https://e/b.git", RemoteProviderId = provider.Id, KeepPreviewData = false,
        });
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var captured = new List<WorktreePreviewStartOptions?>();
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartAsync(WorktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured.Add(o))
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = WorktreePath });
        preview.Setup(p => p.StartServiceAsync(WorktreePath, "web", It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorktreePreviewStartOptions?, CancellationToken>((_, _, o, _) => captured.Add(o))
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = WorktreePath });
        var withRepository = BuildController(
            new WorkItemView { Id = id, Title = "Agent title", WorktreePath = WorktreePath, RepositoryId = editedRepoId, RunRepositoryId = runRepoId },
            preview, new Mock<IWorkItemNotifier>(), db);
        var withoutRepository = BuildController(
            new WorkItemView { Id = id, Title = "Agent title", WorktreePath = WorktreePath },
            preview, new Mock<IWorkItemNotifier>(), db);

        Assert.IsType<OkObjectResult>(await withRepository.StartPreview(id, new WorktreePreviewStartRequest()));
        Assert.IsType<OkObjectResult>(await withRepository.StartPreviewService(id, "web", new WorktreePreviewStartRequest()));
        Assert.IsType<OkObjectResult>(await withoutRepository.StartPreview(id, new WorktreePreviewStartRequest()));

        var expected = new PreviewVolumeScope(runRepoId, true, id, "Agent title");
        Assert.Equal(3, captured.Count);
        Assert.Equal(expected, captured[0]!.Volumes);
        Assert.Equal(expected, captured[1]!.Volumes);
        Assert.Null(captured[2]!.Volumes);
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

    private static async Task<WorktreePreviewServiceResponse> GetAgentService(
        string workItemId, string? runHeader, string? chatHeader, TestDb db)
    {
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.GetStatusAsync(WorktreePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse
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
            });
        var controller = BuildController(ItemWithWorktree(workItemId), preview, new Mock<IWorkItemNotifier>(), db);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (runHeader != null) http.User = ILD.Tests.Integration.AgentTokenTestExtensions.AgentPrincipal(ILD.Data.Entities.AgentTokenSource.Run, Guid.Parse(runHeader));
        else if (chatHeader != null) http.User = ILD.Tests.Integration.AgentTokenTestExtensions.AgentPrincipal(ILD.Data.Entities.AgentTokenSource.Chat, Guid.Parse(chatHeader));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var ok = Assert.IsType<OkObjectResult>(await controller.GetPreview(workItemId));
        return Assert.Single(Assert.IsType<WorktreePreviewResponse>(ok.Value).Services);
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

        var service = await GetAgentService(id, runId.ToString(), null, db);

        Assert.Equal("http://127.0.0.1:5173", service.AgentUrl);
        Assert.Equal("http://wi-7.ild.localhost:8080", service.PublicUrl);
    }

    [Fact]
    public async Task A_run_on_another_work_item_is_refused_its_preview()
    {
        using var db = new TestDb();
        var id = Guid.NewGuid().ToString();
        var runId = SeedRun(db, Guid.NewGuid().ToString());
        var preview = new Mock<IWorktreePreviewService>();
        var controller = BuildController(ItemWithWorktree(id), preview, new Mock<IWorkItemNotifier>(), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = ILD.Tests.Integration.AgentTokenTestExtensions.AgentPrincipal(ILD.Data.Entities.AgentTokenSource.Run, runId),
            },
        };

        var result = await controller.GetPreview(id);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        preview.Verify(p => p.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_chat_session_gets_the_public_address_as_agentUrl()
    {
        using var db = new TestDb();
        var service = await GetAgentService(Guid.NewGuid().ToString(), null, Guid.NewGuid().ToString(), db);

        Assert.Equal("http://wi-7.ild.localhost:8080", service.AgentUrl);
    }

    [Fact]
    public async Task A_call_without_headers_gets_the_public_address_as_agentUrl()
    {
        using var db = new TestDb();
        var service = await GetAgentService(Guid.NewGuid().ToString(), null, null, db);

        Assert.Equal("http://wi-7.ild.localhost:8080", service.AgentUrl);
    }
}
