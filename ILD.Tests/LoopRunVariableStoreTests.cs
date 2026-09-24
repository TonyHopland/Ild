using ILD.Data.DTOs;
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

        var writes = await WritesAsync(db, run.WorkItemId);

        Assert.Equal(new[] { "first", "second" }, writes.Select(w => w.Value));
    }

    [Fact]
    public async Task Each_write_records_the_value_it_replaced_and_none_when_it_created_the_variable()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "first");
        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "second");

        var writes = await WritesAsync(db, run.WorkItemId);

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

        var write = Assert.Single(await WritesAsync(db, run.WorkItemId));
        Assert.Equal("old", write.PreviousValue);
    }

    private static async Task<List<LoopRunVariableWrite>> WritesAsync(TestDb db, string workItemId)
        => await db.Fresh().LoopRunVariableWrites
            .Where(w => w.LoopRun.WorkItemId == workItemId)
            .OrderBy(w => w.WrittenAt)
            .ToListAsync();

    /// <summary>Finishes whatever execution of the run is running and starts the next.</summary>
    private static async Task<Guid> NextTurnAsync(TestDb db, LoopRun run)
    {
        using (var ctx = db.Fresh())
        {
            await ctx.LoopRunNodes
                .Where(rn => rn.LoopRunId == run.Id && rn.Status == LoopRunNodeStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(rn => rn.Status, LoopRunNodeStatus.Succeeded));
        }
        return await SeedRunNodeAsync(db, run, LoopRunNodeStatus.Running);
    }

    [Fact]
    public async Task A_turn_reports_the_value_it_left_and_whether_it_created_or_changed_it()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        var store = new LoopRunStore(db.Fresh());
        var first = await NextTurnAsync(db, run);
        await store.SetVariableAsync(run.Id, "summary", "draft");
        await store.SetVariableAsync(run.Id, "summary", "first pass");
        var second = await NextTurnAsync(db, run);
        await store.SetVariableAsync(run.Id, "summary", "final");

        var changes = await new LoopRunStore(db.Fresh()).GetTurnVariableChangesForWorkItemAsync(run.WorkItemId);

        var byTurn = changes.ToDictionary(c => c.RunNodeId);
        Assert.Equal(2, changes.Count);
        Assert.Equal(("first pass", true, true), (byTurn[first].Value, byTurn[first].Created, byTurn[first].ChangedLater));
        Assert.Equal(("final", false, false), (byTurn[second].Value, byTurn[second].Created, byTurn[second].ChangedLater));
    }

    [Fact]
    public async Task A_turn_that_left_a_variable_as_it_found_it_did_not_change_it()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        var store = new LoopRunStore(db.Fresh());
        await NextTurnAsync(db, run);
        await store.SetVariableAsync(run.Id, "summary", "same");
        await NextTurnAsync(db, run);
        await store.SetVariableAsync(run.Id, "summary", "temp");
        await store.SetVariableAsync(run.Id, "summary", "same");

        var changes = await new LoopRunStore(db.Fresh()).GetTurnVariableChangesForWorkItemAsync(run.WorkItemId);

        // Only the first turn, which created it; the second put it back as it was.
        Assert.True(Assert.Single(changes).Created);
    }

    [Fact]
    public async Task A_variable_set_before_history_was_kept_is_reported_as_changed()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        db.Context.LoopRunVariables.Add(new LoopRunVariable { LoopRunId = run.Id, Name = "handoff", Value = "old" });
        await db.Context.SaveChangesAsync();
        await NextTurnAsync(db, run);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "new");

        var change = Assert.Single(await new LoopRunStore(db.Fresh()).GetTurnVariableChangesForWorkItemAsync(run.WorkItemId));
        Assert.False(change.Created);
    }

    [Fact]
    public async Task Turns_cover_every_run_of_the_work_item_and_no_other_and_runs_do_not_mix()
    {
        using var db = new TestDb();
        var first = await SeedRunAsync(db);
        var retry = await SeedRunAsync(db);
        retry.WorkItemId = first.WorkItemId;
        var other = await SeedRunAsync(db);
        await db.Context.SaveChangesAsync();
        var store = new LoopRunStore(db.Fresh());
        var firstTurn = await NextTurnAsync(db, first);
        await store.SetVariableAsync(first.Id, "handoff", "from first");
        await NextTurnAsync(db, retry);
        await store.SetVariableAsync(retry.Id, "handoff", "from retry");
        await NextTurnAsync(db, other);
        await store.SetVariableAsync(other.Id, "handoff", "elsewhere");

        var changes = await new LoopRunStore(db.Fresh()).GetTurnVariableChangesForWorkItemAsync(first.WorkItemId);

        Assert.Equal(new[] { "from first", "from retry" }, changes.Select(c => c.Value).OrderBy(v => v));
        // The retry's write is a different run's variable, not a later change of this one.
        Assert.False(changes.Single(c => c.RunNodeId == firstTurn).ChangedLater);
    }

    [Fact]
    public async Task A_write_no_execution_made_belongs_to_no_turn()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "value");

        Assert.Empty(await new LoopRunStore(db.Fresh()).GetTurnVariableChangesForWorkItemAsync(run.WorkItemId));
    }

    [Fact]
    public async Task Writes_racing_on_one_variable_each_record_the_value_they_actually_replaced()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", $"v{i}")));

        // The writes form one unbroken chain: each replaced the value the one
        // before it left, and the last is what the variable holds now.
        var writes = await WritesAsync(db, run.WorkItemId);
        Assert.Equal(8, writes.Count);
        Assert.Null(writes[0].PreviousValue);
        for (var i = 1; i < writes.Count; i++)
            Assert.Equal(writes[i - 1].Value, writes[i].PreviousValue);
        var current = Assert.Single(await new LoopRunStore(db.Fresh()).GetVariablesAsync(run.Id));
        Assert.Equal(writes[^1].Value, current.Value);
    }

    [Fact]
    public async Task A_write_is_attributed_to_the_node_execution_running_when_it_landed()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        await SeedRunNodeAsync(db, run, LoopRunNodeStatus.Succeeded);
        var running = await SeedRunNodeAsync(db, run, LoopRunNodeStatus.Running);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "value");

        var write = Assert.Single(await WritesAsync(db, run.WorkItemId));
        Assert.Equal(running, write.RunNodeId);
    }

    [Fact]
    public async Task A_write_with_no_node_running_is_kept_unattributed()
    {
        using var db = new TestDb();
        var run = await SeedRunAsync(db);

        await new LoopRunStore(db.Fresh()).SetVariableAsync(run.Id, "handoff", "value");

        var write = Assert.Single(await WritesAsync(db, run.WorkItemId));
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
