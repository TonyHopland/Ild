using FluentAssertions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;

namespace ILD.Tests;

public class LoopRunEngineFieldsTests
{
    [Fact]
    public async Task LoopRun_persists_and_retrieves_engine_only_fields()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplateVersions.Add(ltv);
        var wi = Guid.NewGuid();
        await db.Context.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = wi,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            RepositoryId = repo.Id,
            WorktreePath = "/tmp/worktrees/test",
            BranchName = "feature/branch",
            PrUrl = "https://example/pulls/1",
            IsPrMerged = false,
            CreatedByLoopRunId = null,
            HumanFeedbackReason = "node failure",
        };
        await db.LoopRuns.CreateRunAsync(run);

        var freshStore = new LoopRunStore(db.Fresh());
        var result = await freshStore.GetByIdAsync(run.Id);

        result.Should().NotBeNull();
        result!.WorktreePath.Should().Be("/tmp/worktrees/test");
        result.BranchName.Should().Be("feature/branch");
        result.PrUrl.Should().Be("https://example/pulls/1");
        result.IsPrMerged.Should().BeFalse();
        result.RepositoryId.Should().Be(repo.Id);
        result.HumanFeedbackReason.Should().Be("node failure");
        result.CreatedByLoopRunId.Should().BeNull();
    }

    [Fact]
    public async Task LoopRun_engine_fields_are_nullable()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplateVersions.Add(ltv);
        var wi = Guid.NewGuid();
        await db.Context.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = wi,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        await db.LoopRuns.CreateRunAsync(run);

        var freshStore = new LoopRunStore(db.Fresh());
        var result = await freshStore.GetByIdAsync(run.Id);

        result.Should().NotBeNull();
        result!.WorktreePath.Should().BeNull();
        result.BranchName.Should().BeNull();
        result.PrUrl.Should().BeNull();
        result.IsPrMerged.Should().BeFalse();
        result.RepositoryId.Should().BeNull();
        result.HumanFeedbackReason.Should().BeNull();
        result.CreatedByLoopRunId.Should().BeNull();
    }
}
