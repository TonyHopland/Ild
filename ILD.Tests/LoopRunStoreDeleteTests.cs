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
}
