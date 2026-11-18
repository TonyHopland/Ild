using ILD.Core.Services.Implementations;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A run an earlier version parked to wait for the processes its agent left running (stored halt reason 5) becomes an ordinary halt
/// for a person at startup; ILD no longer waits for it, and a resume of the node is the way on.
/// </summary>
public class LoopEngineRetiredProcessWaitTests
{
    private const int RetiredHaltReason = 5;

    private static LoopEngineHarness Parked(out LoopRunNode attempt)
    {
        var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Tester");
        var run = h.SeedRun("ai", LoopRunStatus.WaitingHuman, isHalted: true);
        h.Db.Context.LoopRuns.Where(r => r.Id == run.Id).ExecuteUpdate(s => s
            .SetProperty(r => r.HaltReason, (HaltReason?)RetiredHaltReason)
            .SetProperty(r => r.HumanFeedbackReason, "Waiting for ILD"));
        attempt = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = h.NodesById["ai"].Id, Status = LoopRunNodeStatus.WaitingIld, Output = "tests are running" };
        h.Db.Context.LoopRunNodes.Add(attempt);
        h.Db.Context.SaveChanges();
        foreach (var entry in h.Db.Context.ChangeTracker.Entries<LoopRun>().ToList()) entry.Reload();
        return h;
    }

    [Fact]
    public async Task A_run_parked_by_the_old_wait_becomes_an_ordinary_halt_with_a_reason_and_a_node_that_can_be_resumed()
    {
        using var h = Parked(out var attempt);

        Assert.Equal(1, await h.Engine.ParkRunsOfTheRetiredProcessWaitAsync());

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(HaltReason.Human, run.HaltReason);
        Assert.Equal(HumanFeedbackReasons.RunHalted, run.HumanFeedbackReason);
        var node = Assert.Single(h.ReloadRunNodes(), n => n.Id == attempt.Id);
        Assert.Equal(LoopRunNodeStatus.Interrupted, node.Status);
        Assert.Equal("tests are running", node.Output);
        var parked = Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.RunParked);
        Assert.Contains("no longer waits", parked.Data);
        Assert.Contains("Resume", parked.Data);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.Is<string?>(r => r!.Contains("no longer waits")), It.IsAny<string?>(), It.IsAny<Guid?>(), HumanFeedbackReasons.RunHalted), Times.Once);
    }

    [Fact]
    public async Task Converting_twice_leaves_the_run_as_it_is()
    {
        using var h = Parked(out _);
        await h.Engine.ParkRunsOfTheRetiredProcessWaitAsync();

        Assert.Equal(0, await h.Engine.ParkRunsOfTheRetiredProcessWaitAsync());
        Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.RunParked);
    }

    [Fact]
    public async Task A_person_s_own_halt_is_not_converted()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Tester");
        h.SeedRun("ai", LoopRunStatus.WaitingHuman, isHalted: true, HaltReason.Throttled);

        Assert.Equal(0, await h.Engine.ParkRunsOfTheRetiredProcessWaitAsync());
        Assert.Equal(HaltReason.Throttled, h.ReloadRun().HaltReason);
    }
}
