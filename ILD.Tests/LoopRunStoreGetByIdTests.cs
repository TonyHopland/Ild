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
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        Assert.NotNull(result);
        Assert.NotNull(result!.LoopTemplateVersion);
        Assert.Equal(ltv.Id, result.LoopTemplateVersion!.Id);
        Assert.Equal(1, result.LoopTemplateVersion.VersionNumber);
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
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var n1 = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = ln.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-2) };
        var n2 = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = ln.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-1) };
        var n3 = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = ln.Id, CreatedAt = DateTime.UtcNow };

        db.Context.LoopRunNodes.Add(n2);
        db.Context.LoopRunNodes.Add(n1);
        db.Context.LoopRunNodes.Add(n3);
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var freshStore = new LoopRunStore(db.Fresh());
        var result = await freshStore.GetRunNodesAsync(run.Id);

        Assert.Equal(new[] { n1.Id, n2.Id, n3.Id }, result.Select(n => n.Id));
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
            LoopRunId = run.Id,
            EventType = EventType.LoopRunCompleted,
            Timestamp = DateTime.UtcNow,
            Data = "done",
        });
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var freshStore = new LoopRunStore(db.Fresh());
        var deleted = await freshStore.DeleteAsync(run.Id);

        Assert.True(deleted);

        using var verify = db.Fresh();
        Assert.Null((await verify.LoopRuns.FindAsync([run.Id], TestContext.Current.CancellationToken)));
        Assert.Equal(0, (await verify.EventLogs.Where(e => e.LoopRunId == run.Id).CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task GetLatestByWorkItemAsync_returns_the_newest_run_whatever_its_state()
    {
        using var db = new TestDb();
        var version = RunTimeline.SeedVersion(db);
        var now = DateTime.UtcNow;
        LoopRun Add(string workItemId, LoopRunStatus status, DateTime? startedAt, DateTime createdAt)
        {
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                LoopTemplateVersionId = version,
                Status = status,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                StartedAt = startedAt,
                CreatedAt = createdAt,
            };
            db.Context.LoopRuns.Add(run);
            return run;
        }

        // Ordered by when a run started, or was created if it never started —
        // not by either alone, and not by whether it is still alive.
        var item = "WI-" + Guid.NewGuid().ToString("N");
        Add(item, LoopRunStatus.Failed, now.AddHours(-3), now.AddHours(-3));
        Add(item, LoopRunStatus.Running, now.AddHours(-2), now.AddMinutes(-30));
        var newest = Add(item, LoopRunStatus.Completed, null, now.AddHours(-1));
        Add("WI-" + Guid.NewGuid().ToString("N"), LoopRunStatus.Cancelled, now, now);
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = new LoopRunStore(db.Fresh());

        Assert.Equal(newest.Id, (await store.GetLatestByWorkItemAsync(item))?.Id);
        Assert.Null(await store.GetLatestByWorkItemAsync("WI-" + Guid.NewGuid().ToString("N")));
    }
}
