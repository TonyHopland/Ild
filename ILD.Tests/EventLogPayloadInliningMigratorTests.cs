using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Migrations;

namespace ILD.Tests;

public class EventLogPayloadInliningMigratorTests
{
    [Fact]
    public async Task Migrate_pulls_spilled_file_back_into_Data_and_clears_path()
    {
        using var db = new TestDb();
        var file = Path.Combine(Directory.CreateTempSubdirectory("ild-inline-test-").FullName, "1.json");
        var payload = new string('x', 20_000);
        await File.WriteAllTextAsync(file, payload);

        var id = Guid.NewGuid();
        db.Context.EventLogs.Add(new EventLog
        {
            Id = id,
            Sequence = 1,
            EventType = EventType.NodeCompleted,
            Timestamp = DateTime.UtcNow,
            PayloadPath = file,
            Data = null,
        });
        db.Context.SaveChanges();

        try
        {
            var migrated = await EventLogPayloadInliningMigrator.MigrateAsync(db.Context);
            Assert.Equal(1, migrated);

            var row = db.Fresh().EventLogs.Single(e => e.Id == id);
            Assert.Equal(payload, row.Data);
            Assert.Null(row.PayloadPath);

            // Idempotent: a second pass finds nothing left to inline.
            Assert.Equal(0, await EventLogPayloadInliningMigrator.MigrateAsync(db.Fresh()));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Migrate_clears_dangling_path_when_the_spilled_file_is_gone()
    {
        using var db = new TestDb();

        // A row whose payload file the ephemeral /app layer already wiped, plus a
        // normal inline row that must be left untouched.
        var missingId = Guid.NewGuid();
        var inlineId = Guid.NewGuid();
        db.Context.EventLogs.Add(new EventLog
        {
            Id = missingId,
            Sequence = 1,
            EventType = EventType.NodeCompleted,
            Timestamp = DateTime.UtcNow,
            PayloadPath = Path.Combine(Path.GetTempPath(), "ild-missing-" + Guid.NewGuid() + ".json"),
            Data = null,
        });
        db.Context.EventLogs.Add(new EventLog
        {
            Id = inlineId,
            Sequence = 2,
            EventType = EventType.NodeStarted,
            Timestamp = DateTime.UtcNow,
            Data = "already inline",
        });
        db.Context.SaveChanges();

        var migrated = await EventLogPayloadInliningMigrator.MigrateAsync(db.Context);
        Assert.Equal(1, migrated);

        var fresh = db.Fresh();
        var missing = fresh.EventLogs.Single(e => e.Id == missingId);
        Assert.Null(missing.PayloadPath);
        Assert.Null(missing.Data);

        var inline = fresh.EventLogs.Single(e => e.Id == inlineId);
        Assert.Equal("already inline", inline.Data);
        Assert.Null(inline.PayloadPath);
    }
}
