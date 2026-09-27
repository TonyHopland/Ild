using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A work item's repository is editable, and like the branch overrides the edit
/// is never retroactive (ADR-0008): the item reports what it was edited to, while
/// a run that already exists keeps acting on the repository it was created with.
/// </summary>
public class WorkItemsControllerRepositoryTests
{
    private const string WorktreePath = "/tmp/worktrees/repo-edit-wi";

    [Fact]
    public async Task Update_to_another_repository_persists_it()
    {
        var s = Setup();
        using var _db = s.Db;
        var id = await s.Mgr.CreateWorkItemAsync("t", "", s.RepoA);

        var result = await s.Controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = s.RepoB.ToString(),
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(s.RepoB, Assert.IsType<WorkItemView>(ok.Value).RepositoryId);
        var reread = (await s.Mgr.GetWorkItemAsync(id))!;
        Assert.Equal(s.RepoB, reread.RepositoryId);
        // With no run at all, the run-scoped repository is the item's own.
        Assert.Equal(s.RepoB, reread.RunRepositoryId);
    }

    [Theory]
    [InlineData(LoopRunStatus.Running)]
    [InlineData(LoopRunStatus.WaitingHuman)]
    [InlineData(LoopRunStatus.Failed)]
    [InlineData(LoopRunStatus.Cancelled)]
    public async Task Update_persists_the_new_repository_while_the_current_run_keeps_the_old_one(LoopRunStatus status)
    {
        var s = Setup();
        using var _db = s.Db;
        var id = await s.Mgr.CreateWorkItemAsync("t", "", s.RepoA);
        SeedRun(s.Db, id, status, s.RepoA);

        var result = await s.Controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = s.RepoB.ToString(),
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var view = Assert.IsType<WorkItemView>(ok.Value);
        Assert.Equal(s.RepoB, view.RepositoryId);
        Assert.Equal(s.RepoA, view.RunRepositoryId);
        var reread = (await s.Mgr.GetWorkItemAsync(id))!;
        Assert.Equal(s.RepoB, reread.RepositoryId);
        Assert.Equal(s.RepoA, reread.RunRepositoryId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_without_a_repository_leaves_the_stored_one_alone(string? sent)
    {
        var s = Setup();
        using var _db = s.Db;
        var id = await s.Mgr.CreateWorkItemAsync("t", "", s.RepoA);

        var result = await s.Controller.Update(id, new WorkItemCreateRequest
        {
            Title = "renamed",
            RepositoryId = sent!,
        });

        Assert.IsType<OkObjectResult>(result);
        var reread = (await s.Mgr.GetWorkItemAsync(id))!;
        Assert.Equal("renamed", reread.Title);
        Assert.Equal(s.RepoA, reread.RepositoryId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("3f2b8c1e-0000-4000-8000-000000000001")]
    public async Task Update_rejects_a_repository_that_is_unparseable_or_unknown_and_changes_nothing(string sent)
    {
        var s = Setup();
        using var _db = s.Db;
        var id = await s.Mgr.CreateWorkItemAsync("t", "", s.RepoA);

        var result = await s.Controller.Update(id, new WorkItemCreateRequest
        {
            Title = "renamed",
            RepositoryId = sent,
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(string.IsNullOrWhiteSpace(
            bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value) as string));
        var reread = (await s.Mgr.GetWorkItemAsync(id))!;
        Assert.Equal("t", reread.Title);
        Assert.Equal(s.RepoA, reread.RepositoryId);
    }

    [Fact]
    public async Task Preview_of_an_existing_run_keeps_the_env_of_the_repository_it_was_created_on()
    {
        var s = Setup();
        using var _db = s.Db;
        var id = await s.Mgr.CreateWorkItemAsync("t", "", s.RepoA);
        SeedRun(s.Db, id, LoopRunStatus.WaitingHuman, s.RepoA);
        await s.Controller.Update(id, new WorkItemCreateRequest { Title = "t", RepositoryId = s.RepoB.ToString() });
        Assert.Equal(s.RepoB, (await s.Mgr.GetWorkItemAsync(id))!.RepositoryId);

        await s.Controller.StartPreview(id, new WorktreePreviewStartRequest());
        await s.Controller.StartPreviewService(id, "web", new WorktreePreviewStartRequest());

        s.Preview.Verify(p => p.StartAsync(WorktreePath,
            It.Is<WorktreePreviewStartOptions?>(o => o!.CustomEnv == "FROM=repo-a"), It.IsAny<CancellationToken>()), Times.Once);
        s.Preview.Verify(p => p.StartServiceAsync(WorktreePath, "web",
            It.Is<WorktreePreviewStartOptions?>(o => o!.CustomEnv == "FROM=repo-a"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Diff_of_an_existing_run_is_taken_against_the_repository_it_was_created_on()
    {
        var s = Setup();
        using var _db = s.Db;
        var id = await s.Mgr.CreateWorkItemAsync("t", "", s.RepoA);
        SeedRun(s.Db, id, LoopRunStatus.WaitingHuman, s.RepoA);
        await s.Controller.Update(id, new WorkItemCreateRequest { Title = "t", RepositoryId = s.RepoB.ToString() });
        Assert.Equal(s.RepoB, (await s.Mgr.GetWorkItemAsync(id))!.RepositoryId);

        await s.Controller.GetFiles(id);

        s.RepoManager.Verify(r => r.ListWorktreeFilesAsync(WorktreePath, "main-a"), Times.Once);
    }

    private static void SeedRun(TestDb db, string workItemId, LoopRunStatus status, Guid repositoryId)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = status,
            StartedAt = DateTime.UtcNow,
            RepositoryId = repositoryId,
            WorktreePath = WorktreePath,
        });
        db.Context.SaveChanges();
    }

    private sealed record Harness(
        WorkItemsController Controller,
        WorkItemManager Mgr,
        TestDb Db,
        Guid RepoA,
        Guid RepoB,
        Mock<IWorktreePreviewService> Preview,
        Mock<IRepositoryManager> RepoManager);

    private static Harness Setup()
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repoA = new Repository
        {
            Id = Guid.NewGuid(), Name = "repo-a", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo-a.git",
            DefaultBranch = "main-a", PreviewEnv = "FROM=repo-a",
        };
        var repoB = new Repository
        {
            Id = Guid.NewGuid(), Name = "repo-b", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo-b.git",
            DefaultBranch = "main-b", PreviewEnv = "FROM=repo-b",
        };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.AddRange(repoA, repoB);
        db.Context.SaveChanges();

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(1L);

        var mgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object,
            db.Providers,
            eventLog.Object,
            db.LoopRuns,
            db.ServerClient,
            db.ServerOptions,
            engine: new Mock<ILoopEngine>().Object);

        var branchNames = new Mock<IBranchNameOverrideService>();
        branchNames.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchNameVerdict.Usable);

        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = WorktreePath });
        preview.Setup(p => p.StartServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = WorktreePath });
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(r => r.ListWorktreeFilesAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(Array.Empty<WorktreeFileEntry>());

        var controller = new WorkItemsController(
            mgr,
            new Mock<ILoopEngine>().Object,
            preview.Object,
            repoManager.Object,
            db.LoopRuns,
            db.Providers,
            branchNames.Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
            NullLogger<WorkItemsController>.Instance);

        return new Harness(controller, mgr, db, repoA.Id, repoB.Id, preview, repoManager);
    }
}
