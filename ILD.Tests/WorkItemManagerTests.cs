using FluentAssertions;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class WorkItemManagerTests
{
    private static (WorkItemManager mgr, TestDb db, Guid repoId) Setup()
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();
        return (new WorkItemManager(db.WorkItems), db, repo.Id);
    }

    [Fact]
    public async Task CreateWorkItem_lands_in_WorkQueue_when_repo_default_is_WorkQueue()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var repo = await db.Context.Repositories.FindAsync(repoId);
        repo!.DefaultIntakeStatus = WorkItemStatus.WorkQueue;
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("title", "desc", null, repoId);

        var wi = await mgr.GetWorkItemAsync(id);
        wi!.Status.Should().Be(WorkItemStatus.WorkQueue);
    }

    [Fact]
    public async Task CreateWorkItem_lands_in_Backlog_when_repo_default_is_Backlog()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var repo = await db.Context.Repositories.FindAsync(repoId);
        repo!.DefaultIntakeStatus = WorkItemStatus.Backlog;
        await db.Context.SaveChangesAsync();

        var id = await mgr.CreateWorkItemAsync("title", "desc", null, repoId);

        var wi = await mgr.GetWorkItemAsync(id);
        wi!.Status.Should().Be(WorkItemStatus.Backlog);
    }

    [Fact]
    public async Task IsReady_true_for_workitem_with_no_dependencies()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);
        await mgr.TransitionToWorkQueueAsync(id);

        (await mgr.IsReadyAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task IsReady_false_when_dependency_not_merged()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", null, repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", null, repoId);
        await mgr.AddDependencyAsync(child, dep);

        (await mgr.IsReadyAsync(child)).Should().BeFalse();
    }

    [Fact]
    public async Task IsReady_true_after_dependency_marked_merged()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", null, repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", null, repoId);
        await mgr.AddDependencyAsync(child, dep);

        await mgr.LinkPullRequestAsync(dep, "https://forgejo/pr/1");
        await mgr.ManuallyMarkMergedAsync(dep);

        (await mgr.IsReadyAsync(child)).Should().BeTrue();
    }

    [Fact]
    public async Task AddDependency_rejects_self_loop()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);

        var act = async () => await mgr.AddDependencyAsync(id, id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddDependency_rejects_cycle()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var a = await mgr.CreateWorkItemAsync("a", "", null, repoId);
        var b = await mgr.CreateWorkItemAsync("b", "", null, repoId);
        var c = await mgr.CreateWorkItemAsync("c", "", null, repoId);

        await mgr.AddDependencyAsync(b, a);
        await mgr.AddDependencyAsync(c, b);

        var act = async () => await mgr.AddDependencyAsync(a, c);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cycle*");
    }

    [Fact]
    public async Task TransitionToReady_fails_when_dependencies_unmerged()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var dep = await mgr.CreateWorkItemAsync("dep", "", null, repoId);
        var child = await mgr.CreateWorkItemAsync("child", "", null, repoId);
        await mgr.AddDependencyAsync(child, dep);
        await mgr.TransitionToWorkQueueAsync(child);

        var transitioned = await mgr.TransitionToReadyAsync(child);
        transitioned.Should().BeFalse();
        var wi = await mgr.GetWorkItemAsync(child);
        wi!.Status.Should().Be(WorkItemStatus.WorkQueue);
    }

    [Fact]
    public async Task LinkPullRequest_persists_pr_url()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);

        var result = await mgr.LinkPullRequestAsync(id, "https://forgejo/pr/42");

        result.Should().BeTrue();
        var wi = await mgr.GetWorkItemAsync(id);
        wi!.PrUrl.Should().Be("https://forgejo/pr/42");
    }

    [Fact]
    public async Task ManuallyMarkMerged_transitions_workitem_to_Done()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.Running;
        await db.Context.SaveChangesAsync();

        await mgr.LinkPullRequestAsync(id, "https://forgejo/pr/2");
        await mgr.ManuallyMarkMergedAsync(id);

        var after = await mgr.GetWorkItemAsync(id);
        after!.Status.Should().Be(WorkItemStatus.Done);
        after.IsPrMerged.Should().BeTrue();
    }

    [Fact]
    public async Task ManuallyMarkMerged_does_not_set_Done_when_active_LoopRun_exists()
    {
        var (mgr, db, repoId) = Setup();
        using var _ = db;

        var id = await mgr.CreateWorkItemAsync("a", "", null, repoId);
        var wi = await db.Context.WorkItems.FindAsync(id);
        wi!.Status = WorkItemStatus.Running;
        await db.Context.SaveChangesAsync();

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "test" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        await mgr.ManuallyMarkMergedAsync(id);

        var after = await mgr.GetWorkItemAsync(id);
        after!.IsPrMerged.Should().BeTrue();
        after.Status.Should().NotBe(WorkItemStatus.Done);
    }
}
