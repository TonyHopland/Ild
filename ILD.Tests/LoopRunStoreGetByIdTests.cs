using FluentAssertions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class LoopRunStoreGetByIdTests
{
    [Fact]
    public async Task GetByIdAsync_returns_run_with_loop_template_version_loaded()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "testTemplate" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = lt.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(ltv);
        var wi = Guid.NewGuid().ToString();
        await db.Context.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = wi,
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        await db.LoopRuns.CreateRunAsync(run);

        var freshStore = new LoopRunStore(db.Fresh());
        var result = await freshStore.GetByIdAsync(run.Id);

        result.Should().NotBeNull();
        result!.LoopTemplateVersion.Should().NotBeNull();
        result.LoopTemplateVersion!.Id.Should().Be(ltv.Id);
        result.LoopTemplateVersion.VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task GetRunNodesAsync_returns_nodes_ordered_by_CreatedAt_ascending()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        var wi = Guid.NewGuid().ToString();

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplateVersions.Add(ltv);
        var ln = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = ltv.Id, NodeType = NodeType.Start, Label = "Start" };
        db.Context.LoopNodes.Add(ln);
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi, LoopTemplateVersionId = ltv.Id, Status = LoopRunStatus.Running, RecoveryPolicy = RecoveryPolicy.AutoResume };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();

        var n1 = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = ln.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-2) };
        var n2 = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = ln.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-1) };
        var n3 = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = ln.Id, CreatedAt = DateTime.UtcNow };

        db.Context.LoopRunNodes.Add(n2);
        db.Context.LoopRunNodes.Add(n1);
        db.Context.LoopRunNodes.Add(n3);
        await db.Context.SaveChangesAsync();

        var freshStore = new LoopRunStore(db.Fresh());
        var result = await freshStore.GetRunNodesAsync(run.Id);

        result.Select(n => n.Id).Should().Equal(n1.Id, n2.Id, n3.Id);
    }

    [Fact]
    public async Task DeleteAsync_removes_run_with_event_logs()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);

        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        db.Context.LoopTemplates.Add(template);
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplateVersions.Add(version);

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Completed,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.EventLogs.Add(new EventLog
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            Sequence = 1,
            EventType = EventType.LoopRunCompleted,
            Timestamp = DateTime.UtcNow,
            Data = "done",
        });
        await db.Context.SaveChangesAsync();

        var freshStore = new LoopRunStore(db.Fresh());
        var deleted = await freshStore.DeleteAsync(run.Id);

        deleted.Should().BeTrue();

        using var verify = db.Fresh();
        (await verify.LoopRuns.FindAsync(run.Id)).Should().BeNull();
        (await verify.EventLogs.Where(e => e.LoopRunId == run.Id).CountAsync()).Should().Be(0);
    }
}
