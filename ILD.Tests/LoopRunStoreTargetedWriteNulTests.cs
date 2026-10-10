using ILD.Data.Enums;
using ILD.Data.Stores;

namespace ILD.Tests;

/// <summary>
/// The store's targeted writes go past SaveChanges and its NUL scrub, so each
/// one that writes free text strips NUL itself: PostgreSQL text cannot hold it.
/// </summary>
public class LoopRunStoreTargetedWriteNulTests
{
    [Fact]
    public async Task A_run_feedback_reason_is_stored_without_nul()
    {
        using var db = new TestDb();
        var run = RunTimeline.SeedRun(db, "WI-1", RunTimeline.SeedVersion(db), LoopRunStatus.WaitingHuman);

        await db.LoopRuns.SetHumanFeedbackReasonAsync(run.Id, "parked:\0 bad\0 output");

        Assert.Equal("parked: bad output", run.HumanFeedbackReason);
        var stored = await new LoopRunStore(db.Fresh()).GetByIdAsync(run.Id);
        Assert.Equal("parked: bad output", stored!.HumanFeedbackReason);
    }

    [Fact]
    public async Task A_work_item_status_reason_is_stored_without_nul_when_created_and_when_replaced()
    {
        using var db = new TestDb();

        await db.LoopRuns.SetWorkItemStatusReasonAsync("WI-1", "Failed to start run: \0first");
        Assert.Equal("Failed to start run: first", (await db.LoopRuns.GetWorkItemStatusReasonAsync("WI-1"))!.Text);

        await db.LoopRuns.SetWorkItemStatusReasonAsync("WI-1", "Failed to start run: \0second");
        Assert.Equal("Failed to start run: second", (await new LoopRunStore(db.Fresh()).GetWorkItemStatusReasonAsync("WI-1"))!.Text);
    }

    [Fact]
    public async Task A_run_variable_is_stored_without_nul_when_created_and_when_replaced()
    {
        using var db = new TestDb();
        var run = RunTimeline.SeedRun(db, "WI-1", RunTimeline.SeedVersion(db), LoopRunStatus.Running);

        await db.LoopRuns.SetVariableAsync(run.Id, "summary", "dra\0ft");
        await db.LoopRuns.SetVariableAsync(run.Id, "summary", "fin\0al");

        var stored = await new LoopRunStore(db.Fresh()).GetVariablesAsync(run.Id);
        Assert.Equal("final", Assert.Single(stored).Value);
    }
}
