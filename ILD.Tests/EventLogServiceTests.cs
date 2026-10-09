using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class EventLogServiceTests
{
    private static (EventLogService svc, TestDb db, Guid runId, string workItemId) Setup()
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

        var svc = new EventLogService(db.EventLogs);
        return (svc, db, run.Id, workItemId);
    }

    [Fact]
    public async Task Append_returns_the_event_id_and_ids_increase_per_run()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        var s1 = await svc.AppendAsync(runId, EventType.NodeStarted, "first");
        var s2 = await svc.AppendAsync(runId, EventType.NodeCompleted, "second");
        var s3 = await svc.AppendAsync(runId, EventType.NodeStarted, "third");

        Assert.True(s1 < s2 && s2 < s3);
        Assert.Equal(new[] { s1, s2, s3 }, db.Fresh().EventLogs.Where(e => e.LoopRunId == runId).OrderBy(e => e.Id).Select(e => e.Id));
    }

    [Fact]
    public async Task GetByRunId_returns_events_in_the_order_they_were_written()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        await svc.AppendAsync(runId, EventType.NodeStarted, "a");
        await svc.AppendAsync(runId, EventType.NodeCompleted, "b");

        var entries = (await svc.GetByRunIdAsync(runId)).ToList();
        Assert.Equal(2, entries.Count());
        Assert.Equal("a", entries[0].Data);
        Assert.Equal("b", entries[1].Data);
    }

    [Fact]
    public async Task Large_messages_are_stored_inline_in_the_database()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        // Well over the old 10 KB offload threshold: it must still land in the
        // Data column (PostgreSQL TOASTs it) rather than being written to disk.
        var bigMessage = new string('x', 20_000);
        await svc.AppendAsync(runId, EventType.NodeStarted, bigMessage);

        var entry = (await svc.GetByRunIdAsync(runId)).Single();
        Assert.Equal(bigMessage, entry.Data);

        var row = db.Context.EventLogs.Single(e => e.LoopRunId == runId);
        Assert.Equal(bigMessage, row.Data);
        Assert.Null(row.PayloadPath);
    }

    [Fact]
    public async Task CursorPagination_returns_pages_in_id_order_with_the_last_id_as_cursor()
    {
        var (svc, db, runId, _) = Setup();
        using var _ = db;

        var ids = new long[7];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = await svc.AppendAsync(runId, EventType.NodeStarted, $"event-{i + 1}");

        var page1 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: 0, limit: 3);
        Assert.Equal(ids[..3], page1.Entries.Select(e => e.Id));
        Assert.True(page1.HasMore);
        Assert.Equal(ids[2], page1.NextCursor);

        var page2 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: page1.NextCursor, limit: 3);
        Assert.Equal(ids[3..6], page2.Entries.Select(e => e.Id));
        Assert.True(page2.HasMore);
        Assert.Equal(ids[5], page2.NextCursor);

        var page3 = await svc.GetByRunIdAfterCursorAsync(runId, cursor: page2.NextCursor, limit: 3);
        Assert.Equal("event-7", Assert.Single(page3.Entries).Data);
        Assert.False(page3.HasMore);
        Assert.Equal(ids[6], page3.NextCursor);
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
