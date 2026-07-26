using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopRunStoreDeleteTests
{
    [Fact]
    public async Task DeleteAsync_removes_the_run_and_its_event_log_rows()
    {
        using var db = new TestDb();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Completed,
            CompletedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);

        // Payloads now live inline in the Data column — no spilled files to clean up.
        db.Context.EventLogs.Add(new EventLog
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            Sequence = 1,
            EventType = EventType.NodeCompleted,
            Timestamp = DateTime.UtcNow,
            Data = new string('x', 20_000),
        });
        db.Context.SaveChanges();

        Assert.True(await db.LoopRuns.DeleteAsync(run.Id));

        var fresh = db.Fresh();
        Assert.Empty(fresh.EventLogs.Where(e => e.LoopRunId == run.Id));
        Assert.Null(fresh.LoopRuns.FirstOrDefault(r => r.Id == run.Id));
    }

    // ──────────────────────────────────────────────────────────────────
    // PR archive (WI-203). Deleting the run is the one moment its PR link
    // would be lost for good, so the link is recorded against the work item on
    // the way out — the same copy-on-delete the analytics rollup uses.
    // ──────────────────────────────────────────────────────────────────

    private static Guid SeedTerminalRunWithPr(
        TestDb db,
        Guid versionId,
        string workItemId,
        string prUrl,
        DateTime startedAt,
        bool merged = false,
        string? prSnapshot = null)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = versionId,
            Status = LoopRunStatus.Completed,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(5),
            PrUrl = prUrl,
            IsPrMerged = merged,
            PrSnapshot = prSnapshot,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run.Id;
    }

    private static Guid SeedTemplateVersion(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.SaveChanges();
        return version.Id;
    }

    [Fact]
    public async Task DeleteAsync_archives_the_runs_pull_request_against_its_work_item()
    {
        using var db = new TestDb();
        var versionId = SeedTemplateVersion(db);
        var startedAt = DateTime.UtcNow.AddHours(-2);
        var runId = SeedTerminalRunWithPr(db, versionId, "wi-1", "https://forgejo/repo/pulls/1", startedAt,
            merged: true, prSnapshot: "{\"state\":\"closed\"}");

        Assert.True(await db.LoopRuns.DeleteAsync(runId));

        var record = Assert.Single(await db.LoopRuns.GetArchivedPullRequestsAsync(new[] { "wi-1" }));
        Assert.Equal("https://forgejo/repo/pulls/1", record.Url);
        Assert.Equal(runId, record.LoopRunId);
        Assert.True(record.Merged);
        Assert.Equal("{\"state\":\"closed\"}", record.PrSnapshot);
        Assert.Equal(startedAt, record.FirstSeenAt, TimeSpan.FromSeconds(1));
        Assert.Equal(startedAt, record.LastSeenAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeleteAsync_ignores_a_run_that_never_opened_a_PR()
    {
        using var db = new TestDb();
        var versionId = SeedTemplateVersion(db);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            LoopTemplateVersionId = versionId,
            Status = LoopRunStatus.Failed,
            CompletedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();

        Assert.True(await db.LoopRuns.DeleteAsync(run.Id));

        Assert.Empty(await db.LoopRuns.GetArchivedPullRequestsAsync(new[] { "wi-1" }));
    }

    // The sweeper reaches runs in completion order, a human deleting runs by
    // hand in any order at all — so folding two runs that share a PR must not
    // depend on which of them is deleted first.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteAsync_folds_runs_sharing_a_PR_into_one_record(bool newestFirst)
    {
        using var db = new TestDb();
        var versionId = SeedTemplateVersion(db);
        var older = DateTime.UtcNow.AddHours(-3);
        var newer = DateTime.UtcNow.AddHours(-1);
        const string url = "https://forgejo/repo/pulls/5";
        // The older run is the one that saw the merge, the newer one the one
        // that polled a snapshot: the record has to end up with both.
        var olderRunId = SeedTerminalRunWithPr(db, versionId, "wi-1", url, older, merged: true);
        var newerRunId = SeedTerminalRunWithPr(db, versionId, "wi-1", url, newer, prSnapshot: "{\"state\":\"open\"}");

        foreach (var runId in newestFirst ? new[] { newerRunId, olderRunId } : new[] { olderRunId, newerRunId })
            Assert.True(await db.LoopRuns.DeleteAsync(runId));

        var record = Assert.Single(await db.LoopRuns.GetArchivedPullRequestsAsync(new[] { "wi-1" }));
        Assert.Equal(url, record.Url);
        // Attributed to the newest run that carried it, first seen when the
        // oldest one started, and merged whichever order they arrived in.
        Assert.Equal(newerRunId, record.LoopRunId);
        Assert.Equal(older, record.FirstSeenAt, TimeSpan.FromSeconds(1));
        Assert.Equal(newer, record.LastSeenAt, TimeSpan.FromSeconds(1));
        Assert.True(record.Merged);
        Assert.Equal("{\"state\":\"open\"}", record.PrSnapshot);
    }

    [Fact]
    public async Task Archived_pull_requests_are_scoped_to_the_work_item_and_droppable()
    {
        using var db = new TestDb();
        var versionId = SeedTemplateVersion(db);
        var mine = SeedTerminalRunWithPr(db, versionId, "wi-1", "https://forgejo/repo/pulls/1", DateTime.UtcNow.AddHours(-2));
        var theirs = SeedTerminalRunWithPr(db, versionId, "wi-2", "https://forgejo/repo/pulls/2", DateTime.UtcNow.AddHours(-2));
        Assert.True(await db.LoopRuns.DeleteAsync(mine));
        Assert.True(await db.LoopRuns.DeleteAsync(theirs));

        Assert.Equal(2, (await db.LoopRuns.GetArchivedPullRequestsAsync(new[] { "wi-1", "wi-2" })).Count);
        Assert.Empty(await db.LoopRuns.GetArchivedPullRequestsAsync(Array.Empty<string>()));

        // Deleting the work item is the only thing that drops them — and it
        // takes only that item's.
        Assert.Equal(1, await db.LoopRuns.DeleteArchivedPullRequestsAsync("wi-1"));
        var left = Assert.Single(await db.LoopRuns.GetArchivedPullRequestsAsync(new[] { "wi-1", "wi-2" }));
        Assert.Equal("wi-2", left.WorkItemId);
    }
}
