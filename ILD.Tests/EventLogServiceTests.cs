using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class EventLogServiceTests
{
    private static (EventLogService svc, TestDb db, Guid runId, string workItemId) Setup(string? payloadDir = null)
    {
        var db = new TestDb();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume };

        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();

        var opts = payloadDir != null ? new EventLogOptions { PayloadDirectory = payloadDir } : null;
        var svc = new EventLogService(db.EventLogs, db.LoopRuns, opts);
        return (svc, db, run.Id, workItemId);
    }

    [Fact]
    public async Task Append_returns_monotonically_increasing_sequence_per_run()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        var s1 = await svc.AppendAsync(runId, "NodeStarted", "first");
        var s2 = await svc.AppendAsync(runId, "NodeCompleted", "second");
        var s3 = await svc.AppendAsync(runId, "NodeStarted", "third");

        Assert.Equal(1, s1);
        Assert.Equal(2, s2);
        Assert.Equal(3, s3);
    }

    [Fact]
    public async Task GetByRunId_returns_events_in_sequence_order()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        await svc.AppendAsync(runId, "NodeStarted", "a");
        await svc.AppendAsync(runId, "NodeCompleted", "b");

        var entries = (await svc.GetByRunIdAsync(runId)).ToList();
        Assert.Equal(2, entries.Count());
        Assert.Equal("a", entries[0].Data);
        Assert.Equal("b", entries[1].Data);
    }

    [Fact]
    public async Task Large_messages_are_spilled_to_disk_under_payload_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ild-test-" + Guid.NewGuid());
        var (svc, db, runId, _) = Setup(dir);
        using var _ = db;

        var bigMessage = new string('x', 20_000);
        await svc.AppendAsync(runId, "NodeStarted", bigMessage);

        var entry = (await svc.GetByRunIdAsync(runId)).Single();
        Assert.False(string.IsNullOrEmpty(entry.PayloadPath));
        Assert.StartsWith(dir, entry.PayloadPath!);
        Assert.True(File.Exists(entry.PayloadPath));
        Assert.Equal(bigMessage, (await File.ReadAllTextAsync(entry.PayloadPath)));
        Assert.Empty(entry.Data);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task EnforceRetentionPolicy_deletes_only_events_for_eligible_runs()
    {
        var (svc, db, runIdA, _) = Setup();
        using var _ = db;

        var runB = new LoopRun { Id = Guid.NewGuid(), WorkItemId = Guid.NewGuid().ToString(), LoopTemplateVersionId = db.Context.LoopTemplateVersions.First().Id, RecoveryPolicy = RecoveryPolicy.Cancel };
        db.Context.LoopRuns.Add(runB);
        db.Context.SaveChanges();

        await svc.AppendAsync(runIdA, "NodeStarted", "eligible");
        await svc.AppendAsync(runB.Id, "NodeStarted", "preserved");

        foreach (var e in db.Context.EventLogs)
            e.Timestamp = DateTime.UtcNow.AddDays(-30);
        db.Context.SaveChanges();

        var removed = await svc.EnforceRetentionPolicyAsync(DateTimeOffset.UtcNow.AddDays(-1), new HashSet<Guid> { runIdA });

        Assert.Equal(1, removed);
        Assert.Empty((await svc.GetByRunIdAsync(runIdA)));
        Assert.Single((await svc.GetByRunIdAsync(runB.Id)));
    }

    [Fact]
    public async Task EnforceRetentionPolicy_with_empty_eligible_set_deletes_nothing()
    {
        var (svc, db, runId, _) = Setup();
        using var _d = db;

        await svc.AppendAsync(runId, "NodeStarted", "anything");
        foreach (var e in db.Context.EventLogs)
            e.Timestamp = DateTime.UtcNow.AddDays(-30);
        db.Context.SaveChanges();

        var removed = await svc.EnforceRetentionPolicyAsync(DateTimeOffset.UtcNow.AddDays(-1), new HashSet<Guid>());

        Assert.Equal(0, removed);
        Assert.Single((await svc.GetByRunIdAsync(runId)));
    }

    [Fact]
    public async Task CursorPagination_returns_pages_in_sequence_order_with_correct_cursor()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        for (var i = 1; i <= 7; i++)
            await svc.AppendAsync(runId, "NodeStarted", $"event-{i}");

        var page1 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 0, limit: 3);
        Assert.Equal(3, page1.Entries.Count());
        Assert.Equal(1, page1.Entries[0].Sequence);
        Assert.Equal(3, page1.Entries[2].Sequence);
        Assert.True(page1.HasMore);
        Assert.Equal(3, page1.NextCursor);

        var page2 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 3, limit: 3);
        Assert.Equal(3, page2.Entries.Count());
        Assert.Equal(4, page2.Entries[0].Sequence);
        Assert.Equal(6, page2.Entries[2].Sequence);
        Assert.True(page2.HasMore);
        Assert.Equal(6, page2.NextCursor);

        var page3 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 6, limit: 3);
        Assert.Single(page3.Entries);
        Assert.Equal(7, page3.Entries[0].Sequence);
        Assert.False(page3.HasMore);
        Assert.Equal(7, page3.NextCursor);
    }

    [Fact]
    public async Task CursorPagination_empty_run_returns_empty_page()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        var page = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 0, limit: 10);
        Assert.Empty(page.Entries);
        Assert.False(page.HasMore);
        Assert.Equal(0, page.NextCursor);
    }
}
