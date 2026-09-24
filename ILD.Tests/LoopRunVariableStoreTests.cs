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


    private static async Task<Guid> SeedRunNodeAsync(TestDb db, LoopRun run, LoopRunNodeStatus status)
    {
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = run.LoopTemplateVersionId,
            NodeType = NodeType.AI,
            Label = "ai",
        };
        db.Context.LoopNodes.Add(node);
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = node.Id,
            Status = status,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRunNodes.Add(runNode);
        await db.Context.SaveChangesAsync();
        return runNode.Id;
    }

    [Fact]
    public async Task Every_write_is_kept_in_the_history_after_the_variable_is_overwritten()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "first");
        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "second");

        var writes = await new LoopRunStore(db.Fresh()).GetVariableWritesForWorkItemAsync(run.WorkItemId);

        Assert.Equal(new[] { "first", "second" }, writes.Select(w => w.Value));
    }

    [Fact]
    public async Task Each_write_records_the_value_it_replaced_and_none_when_it_created_the_variable()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "first");
        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "second");

        var writes = await new LoopRunStore(db.Fresh()).GetVariableWritesForWorkItemAsync(run.WorkItemId);

        Assert.Equal(new string?[] { null, "first" }, writes.Select(w => w.PreviousValue));
    }

    [Fact]
    public async Task A_variable_set_before_history_was_kept_is_recorded_as_changed_not_created()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        db.Context.LoopRunVariables.Add(new LoopRunVariable { LoopRunId = run.Id, Name = "handoff", Value = "old" });
        await db.Context.SaveChangesAsync();

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "new");

        var write = Assert.Single(await new LoopRunStore(db.Fresh()).GetVariableWritesForWorkItemAsync(run.WorkItemId));
        Assert.Equal("old", write.PreviousValue);
    }

    [Fact]
    public async Task The_history_covers_every_run_of_the_work_item_and_no_other()
    {
        using var db = new TestDb();
        var first = await SeedRunAsync(db);
        var retry = await SeedRunAsync(db);
        retry.WorkItemId = first.WorkItemId;
        var other = await SeedRunAsync(db);
        await db.Context.SaveChangesAsync();
        var store = new LoopRunStore(db.Fresh());

        await store.SetVariableAsync(first.Id, "handoff", "from first");
        await store.SetVariableAsync(retry.Id, "handoff", "from retry");
        await store.SetVariableAsync(other.Id, "handoff", "elsewhere");

        var writes = await new LoopRunStore(db.Fresh()).GetVariableWritesForWorkItemAsync(first.WorkItemId);

        Assert.Equal(new[] { "from first", "from retry" }, writes.Select(w => w.Value));
    }

    [Fact]
    public async Task A_write_is_attributed_to_the_node_execution_running_when_it_landed()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        await SeedRunNodeAsync(db, run, LoopRunNodeStatus.Succeeded);
        var running = await SeedRunNodeAsync(db, run, LoopRunNodeStatus.Running);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "value");

        var write = Assert.Single(await new LoopRunStore(db.Fresh()).GetVariableWritesForWorkItemAsync(run.WorkItemId));
        Assert.Equal(running, write.RunNodeId);
    }

    [Fact]
    public async Task A_write_with_no_node_running_is_kept_unattributed()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "value");

        var write = Assert.Single(await new LoopRunStore(db.Fresh()).GetVariableWritesForWorkItemAsync(run.WorkItemId));
        Assert.Null(write.RunNodeId);
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
        Assert.Equal(0, await verify.LoopRunVariableWrites.Where(w => w.LoopRunId == run.Id).CountAsync());
    }
}
