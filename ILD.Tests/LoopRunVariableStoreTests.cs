using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class LoopRunVariableStoreTests
{
    private static async Task<LoopRun> SeedRunAsync(TestDb db)
    {
        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        db.Context.LoopTemplates.Add(lt);
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplateVersions.Add(ltv);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = ltv.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();
        return run;
    }

    [Fact]
    public async Task SetVariableAsync_creates_then_overwrites_by_name()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "first");
        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "second");

        var vars = await new LoopRunStore(db.Fresh()).GetVariablesAsync(run.Id);

        var only = Assert.Single(vars);
        Assert.Equal("handoff", only.Name);
        Assert.Equal("second", only.Value);
    }

    [Fact]
    public async Task GetVariablesAsync_returns_all_variables_for_the_run_ordered_by_name()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        var store = new LoopRunStore(db.Fresh());

        await store.SetVariableAsync(run.Id, "summary", "did things");
        await store.SetVariableAsync(run.Id, "changelog", "- thing");

        var vars = await new LoopRunStore(db.Fresh()).GetVariablesAsync(run.Id);

        Assert.Equal(new[] { "changelog", "summary" }, vars.Select(v => v.Name));
    }

    [Fact]
    public async Task GetVariablesAsync_returns_empty_for_a_run_without_variables()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        var vars = await new LoopRunStore(db.Fresh()).GetVariablesAsync(run.Id);

        Assert.Empty(vars);
    }

    [Fact]
    public async Task GetVariablesAsync_returns_fresh_value_after_out_of_scope_update()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "summary", "stale");

        // A long-lived engine context: the first read tracks the variable row.
        var engineStore = new LoopRunStore(db.Context);
        var first = await engineStore.GetVariablesAsync(run.Id);
        Assert.Equal("stale", Assert.Single(first).Value);

        // A separate scope (mirrors the set_loop_variable MCP tool) overwrites it.
        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "summary", "fresh");

        // The same engine context must observe the new value, not the tracked stale one.
        var second = await engineStore.GetVariablesAsync(run.Id);
        Assert.Equal("fresh", Assert.Single(second).Value);
    }

    [Fact]
    public async Task Variables_are_scoped_per_run()
    {
        using var db = new TestDb();
        var runA = await SeedRunAsync(db);
        var runB = await SeedRunAsync(db);
        var store = new LoopRunStore(db.Fresh());

        await store.SetVariableAsync(runA.Id, "shared", "A value");
        await store.SetVariableAsync(runB.Id, "shared", "B value");

        var aVars = await new LoopRunStore(db.Fresh()).GetVariablesAsync(runA.Id);
        Assert.Equal("A value", Assert.Single(aVars).Value);
    }

    [Fact]
    public async Task Deleting_a_run_cascades_to_its_variables()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "value");

        run.Status = LoopRunStatus.Completed;
        run.CompletedAt = DateTime.UtcNow;
        await new LoopRunStore(db.Fresh()).UpdateRunAsync(run);

        var deleted = await new LoopRunStore(db.Fresh()).DeleteAsync(run.Id);
        Assert.True(deleted);

        using var verify = db.Fresh();
        Assert.Equal(0, await verify.LoopRunVariables.Where(v => v.LoopRunId == run.Id).CountAsync());
    }
}
