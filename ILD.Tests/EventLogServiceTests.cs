using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Moq;

namespace ILD.Tests;

public class EventLogServiceTests
{
    private static (EventLogService svc, TestDb db, Guid runId, string workItemId) Setup()
        => SetupOn(new TestDb(), new NoopRunNotifier());

    private static (EventLogService svc, TestDb db, Guid runId, string workItemId) SetupOn(TestDb db, IRunNotifier notifier)
    {
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

        var svc = new EventLogService(db.EventLogs, notifier);
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

    private sealed record Sent(Guid RunId, long Id, string EventType, Guid? NodeId, Guid? RunNodeId, DateTime Timestamp, bool Stored);

    private static Mock<IRunNotifier> Recorder(TestDb db, List<Sent> sent)
    {
        var notifier = new Mock<IRunNotifier>();
        notifier.Setup(n => n.EventLoggedAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime>()))
            .Callback<Guid, long, string, Guid?, Guid?, DateTime>((run, id, type, node, runNode, at) =>
                sent.Add(new Sent(run, id, type, node, runNode, at, db.Fresh().EventLogs.Any(e => e.Id == id))))
            .Returns(Task.CompletedTask);
        return notifier;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_stored_event_is_announced_once_with_its_id_type_nodes_and_stored_timestamp(bool alongside)
    {
        var sent = new List<Sent>();
        var db = new TestDb();
        using var _ = db;
        var (svc, _, runId, _) = SetupOn(db, Recorder(db, sent).Object);
        var nodeId = Guid.NewGuid();
        var runNodeId = Guid.NewGuid();

        var first = alongside
            ? await svc.AppendAlongsideAsync(runId, EventType.NodeStarted, "a prompt", nodeId, runNodeId, null, () => Task.CompletedTask)
            : await svc.AppendAsync(runId, EventType.NodeStarted, "a prompt", nodeId, runNodeId);
        var second = alongside
            ? await svc.AppendAlongsideAsync(runId, EventType.HumanFeedbackReceived, "yes", null, null, null, () => Task.CompletedTask)
            : await svc.AppendAsync(runId, EventType.HumanFeedbackReceived, "yes");

        var rows = db.Fresh().EventLogs.Where(e => e.LoopRunId == runId).OrderBy(e => e.Id).ToList();
        Assert.Equal(
            new[]
            {
                new Sent(runId, first, "NodeStarted", nodeId, runNodeId, rows[0].Timestamp, true),
                new Sent(runId, second, "HumanFeedbackReceived", null, null, rows[1].Timestamp, true),
            },
            sent);
        Assert.Equal(new[] { first, second }, rows.Select(r => r.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_append_announces_nothing(bool alongside)
    {
        var sent = new List<Sent>();
        var db = new TestDb();
        using var _ = db;
        var (svc, _, runId, _) = SetupOn(db, Recorder(db, sent).Object);

        if (alongside)
            await Assert.ThrowsAnyAsync<Exception>(() => svc.AppendAlongsideAsync(runId, EventType.NodeStarted, "x", null, null, null,
                () => throw new InvalidOperationException("the write beside it failed")));
        else
            await Assert.ThrowsAnyAsync<Exception>(() => svc.AppendAsync(Guid.NewGuid(), EventType.NodeStarted, "no such run"));

        Assert.Empty(sent);
        Assert.Empty(db.Fresh().EventLogs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_notifier_never_fails_the_append(bool throwsSynchronously)
    {
        var db = new TestDb();
        using var _ = db;
        var notifier = new Mock<IRunNotifier>();
        var setup = notifier.Setup(n => n.EventLoggedAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime>()));
        if (throwsSynchronously)
            setup.Throws(new InvalidOperationException("hub down"));
        else
            setup.ThrowsAsync(new InvalidOperationException("hub down"));
        var (svc, _, runId, _) = SetupOn(db, notifier.Object);

        var id = await svc.AppendAsync(runId, EventType.NodeStarted, "still written");
        var alongsideId = await svc.AppendAlongsideAsync(runId, EventType.NodeCompleted, "also written", null, null, null, () => Task.CompletedTask);

        Assert.Equal(new[] { id, alongsideId }, db.Fresh().EventLogs.Where(e => e.LoopRunId == runId).OrderBy(e => e.Id).Select(e => e.Id));
    }
}
