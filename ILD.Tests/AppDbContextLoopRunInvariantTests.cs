using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The save-boundary guard for issue #39: a <see cref="LoopRun"/> that is
/// <see cref="LoopRunStatus.Running"/> can never carry a completion timestamp.
/// Enforcing it in <c>AppDbContext.SaveChanges</c> makes the invalid "completed
/// yet running" state unpersistable regardless of which code path produced it.
/// </summary>
public class AppDbContextLoopRunInvariantTests
{
    [Fact]
    public void Running_run_cannot_persist_a_completion_timestamp()
    {
        using var db = new TestDb();
        var run = SeedRun(db, LoopRunStatus.Running);

        // Simulate a retry/resume path that flipped a finished run back to Running
        // without clearing the terminal timestamp it was finalized with.
        run.CompletedAt = DateTime.UtcNow;
        db.Context.SaveChanges();

        var reloaded = db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == run.Id);
        Assert.Equal(LoopRunStatus.Running, reloaded.Status);
        Assert.Null(reloaded.CompletedAt);
    }

    [Fact]
    public void Terminal_run_keeps_its_completion_timestamp()
    {
        using var db = new TestDb();
        var run = SeedRun(db, LoopRunStatus.Running);

        var completedAt = DateTime.UtcNow;
        run.Status = LoopRunStatus.Completed;
        run.CompletedAt = completedAt;
        db.Context.SaveChanges();

        var reloaded = db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == run.Id);
        Assert.Equal(LoopRunStatus.Completed, reloaded.Status);
        Assert.NotNull(reloaded.CompletedAt);
    }

    private static LoopRun SeedRun(TestDb db, LoopRunStatus status)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = status,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }
}
