using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;

namespace ILD.Tests;

public class AppDbContextNullScrubTests
{
    [Fact]
    public async Task SaveChanges_strips_nul_bytes_from_string_columns()
    {
        // Defense-in-depth backstop: a NUL (U+0000) cannot be stored in a
        // PostgreSQL text/varchar column, so any value carrying one — e.g. a
        // coding-agent CLI's raw terminal output forwarded verbatim as a node
        // output — must be scrubbed at the save boundary regardless of which code
        // path produced it, otherwise SaveChanges throws and the run crashes
        // before recording its result.
        using var db = new TestDb();

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplateVersions.Add(ltv);
        var ln = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = ltv.Id, NodeType = NodeType.AI, Label = "AI" };
        db.Context.LoopNodes.Add(ln);
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = Guid.NewGuid().ToString(), LoopTemplateVersionId = ltv.Id, Status = LoopRunStatus.Running, RecoveryPolicy = RecoveryPolicy.AutoResume };
        db.Context.LoopRuns.Add(run);

        var node = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = ln.Id,
            Status = LoopRunNodeStatus.Succeeded,
            Output = "before\0after",
            Error = "err\0or",
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopRunNodes.Add(node);
        await db.Context.SaveChangesAsync();

        var fresh = new LoopRunStore(db.Fresh());
        var reloaded = await fresh.GetRunNodeByIdAsync(node.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("beforeafter", reloaded!.Output);
        Assert.Equal("error", reloaded.Error);
        Assert.DoesNotContain('\0', reloaded.Output!);
    }

    [Fact]
    public async Task SaveChanges_leaves_clean_strings_untouched()
    {
        using var db = new TestDb();

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "clean name" };
        db.Context.LoopTemplates.Add(lt);
        await db.Context.SaveChangesAsync();

        var fresh = db.Fresh();
        var reloaded = await fresh.LoopTemplates.FindAsync(lt.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("clean name", reloaded!.Name);
    }
}
