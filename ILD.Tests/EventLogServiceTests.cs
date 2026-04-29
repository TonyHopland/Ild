using FluentAssertions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class EventLogServiceTests
{
    private static (EventLogService svc, TestDb db, Guid runId) Setup(string? payloadDir = null)
    {
        var db = new TestDb();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = "AutoResume" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        var workItem = new WorkItem { Id = Guid.NewGuid(), Title = "wi", RepositoryId = repo.Id, Status = WorkItemStatus.Running };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItem.Id, LoopTemplateVersionId = version.Id, RecoveryPolicy = "AutoResume" };

        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.WorkItems.Add(workItem);
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();

        var opts = payloadDir != null ? new EventLogOptions { PayloadDirectory = payloadDir } : null;
        var svc = new EventLogService(db.EventLogs, db.LoopRuns, opts);
        return (svc, db, run.Id);
    }

    [Fact]
    public async Task Append_returns_monotonically_increasing_sequence_per_run()
    {
        var (svc, db, runId) = Setup();
        using var _ = db;

        var s1 = await svc.AppendAsync(runId, "NodeStarted", "first");
        var s2 = await svc.AppendAsync(runId, "NodeCompleted", "second");
        var s3 = await svc.AppendAsync(runId, "NodeStarted", "third");

        s1.Should().Be(1);
        s2.Should().Be(2);
        s3.Should().Be(3);
    }

    [Fact]
    public async Task GetByRunId_returns_events_in_sequence_order()
    {
        var (svc, db, runId) = Setup();
        using var _ = db;

        await svc.AppendAsync(runId, "NodeStarted", "a");
        await svc.AppendAsync(runId, "NodeCompleted", "b");

        var entries = (await svc.GetByRunIdAsync(runId)).ToList();
        entries.Should().HaveCount(2);
        entries[0].Data.Should().Be("a");
        entries[1].Data.Should().Be("b");
    }

    [Fact]
    public async Task Large_messages_are_spilled_to_disk_under_payload_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ild-test-" + Guid.NewGuid());
        var (svc, db, runId) = Setup(dir);
        using var _ = db;

        var bigMessage = new string('x', 20_000);
        await svc.AppendAsync(runId, "NodeStarted", bigMessage);

        var entry = (await svc.GetByRunIdAsync(runId)).Single();
        entry.PayloadPath.Should().NotBeNullOrEmpty();
        entry.PayloadPath!.Should().StartWith(dir);
        File.Exists(entry.PayloadPath).Should().BeTrue();
        (await File.ReadAllTextAsync(entry.PayloadPath)).Should().Be(bigMessage);
        entry.Data.Should().BeEmpty();

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task EnforceRetentionPolicy_removes_old_entries_but_preserves_failed_runs()
    {
        var (svc, db, runId) = Setup();
        using var _ = db;

        var failedRun = new LoopRun { Id = Guid.NewGuid(), WorkItemId = db.Context.WorkItems.First().Id, LoopTemplateVersionId = db.Context.LoopTemplateVersions.First().Id, RecoveryPolicy = "Cancel", Status = LoopRunStatus.Failed };
        db.Context.LoopRuns.Add(failedRun);
        db.Context.SaveChanges();

        await svc.AppendAsync(runId, "NodeStarted", "succ");
        await svc.AppendAsync(failedRun.Id, "NodeFailed", "boom");

        foreach (var e in db.Context.EventLogs)
            e.Timestamp = DateTime.UtcNow.AddDays(-30);
        db.Context.SaveChanges();

        var removed = await svc.EnforceRetentionPolicyAsync(DateTimeOffset.UtcNow.AddDays(-1));

        removed.Should().Be(1);
        (await svc.GetByRunIdAsync(runId)).Should().BeEmpty();
        (await svc.GetByRunIdAsync(failedRun.Id)).Should().HaveCount(1);
    }

    [Fact]
    public async Task CursorPagination_returns_pages_in_sequence_order_with_correct_cursor()
    {
        var (svc, db, runId) = Setup();
        using var _ = db;

        for (var i = 1; i <= 7; i++)
            await svc.AppendAsync(runId, "NodeStarted", $"event-{i}");

        var page1 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 0, limit: 3);
        page1.Entries.Should().HaveCount(3);
        page1.Entries[0].Sequence.Should().Be(1);
        page1.Entries[2].Sequence.Should().Be(3);
        page1.HasMore.Should().BeTrue();
        page1.NextCursor.Should().Be(3);

        var page2 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 3, limit: 3);
        page2.Entries.Should().HaveCount(3);
        page2.Entries[0].Sequence.Should().Be(4);
        page2.Entries[2].Sequence.Should().Be(6);
        page2.HasMore.Should().BeTrue();
        page2.NextCursor.Should().Be(6);

        var page3 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 6, limit: 3);
        page3.Entries.Should().HaveCount(1);
        page3.Entries[0].Sequence.Should().Be(7);
        page3.HasMore.Should().BeFalse();
        page3.NextCursor.Should().Be(7);
    }

    [Fact]
    public async Task CursorPagination_empty_run_returns_empty_page()
    {
        var (svc, db, runId) = Setup();
        using var _ = db;

        var page = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 0, limit: 10);
        page.Entries.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.NextCursor.Should().Be(0);
    }
}
