using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopRunStoreDeleteTests
{
    [Fact]
    public async Task DeleteAsync_removes_spilled_payload_files_and_their_directory()
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

        // Simulate EventLogService's large-payload spill layout: {dir}/{runId}/{seq}.json
        var payloadRoot = Directory.CreateTempSubdirectory("ild-payload-test-").FullName;
        var runDir = Path.Combine(payloadRoot, run.Id.ToString());
        Directory.CreateDirectory(runDir);
        var payloadPath = Path.Combine(runDir, "1.json");
        await File.WriteAllTextAsync(payloadPath, "{\"big\":true}");

        db.Context.EventLogs.Add(new EventLog
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            Sequence = 1,
            EventType = EventType.NodeCompleted,
            Timestamp = DateTime.UtcNow,
            PayloadPath = payloadPath,
        });
        db.Context.SaveChanges();

        try
        {
            Assert.True(await db.LoopRuns.DeleteAsync(run.Id));

            // Rows and spilled files are both gone — deleting only the rows
            // would orphan the payload files on disk forever.
            Assert.False(File.Exists(payloadPath));
            Assert.False(Directory.Exists(runDir));
            Assert.Empty(db.Fresh().EventLogs.Where(e => e.LoopRunId == run.Id));
        }
        finally
        {
            if (Directory.Exists(payloadRoot)) Directory.Delete(payloadRoot, recursive: true);
        }
    }
}
