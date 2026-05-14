using FluentAssertions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;

namespace ILD.Tests;

public class LoopRunNoWorkItemFKTests
{
    [Fact]
    public async Task LoopRun_persists_without_WorkItem_row()
    {
        using var db = new TestDb();

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplateVersions.Add(ltv);
        await db.Context.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            RepositoryId = Guid.NewGuid(),
            WorktreePath = "/tmp/worktrees/test",
            BranchName = "feature/branch",
        };
        await db.LoopRuns.CreateRunAsync(run);

        var freshStore = new LoopRunStore(db.Fresh());
        var result = await freshStore.GetByIdAsync(run.Id);

        result.Should().NotBeNull();
        result!.WorkItemId.Should().Be(run.WorkItemId);
        result.WorktreePath.Should().Be("/tmp/worktrees/test");
        result.BranchName.Should().Be("feature/branch");
    }
}
