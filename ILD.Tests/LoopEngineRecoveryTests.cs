using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopEngineRecoveryTests
{
    [Fact]
    public async Task ResumeRecoveredRunAsync_marks_stale_Running_run_nodes_as_Interrupted()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        // Register an executor that immediately terminates so the relaunched run parks cleanly.
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("re-entry"),
            new NodeOutcome.Terminal("done")));

        var run = h.SeedRun("a");
        var staleRunNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = h.NodesById["a"].Id,
            NodeLabel = "a",
            Status = LoopRunNodeStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        h.Db.Context.LoopRunNodes.Add(staleRunNode);
        h.Db.Context.SaveChanges();

        await h.Engine.ResumeRecoveredRunAsync(h.RunId);

        // The Running→Interrupted transition must happen synchronously before the
        // background relaunch. Read via a fresh context to bypass tracker cache.
        using var fresh = h.Db.Fresh();
        var reloaded = fresh.LoopRunNodes.Single(rn => rn.Id == staleRunNode.Id);
        Assert.Equal(LoopRunNodeStatus.Interrupted, reloaded.Status);
        Assert.NotNull(reloaded.CompletedAt);
    }
}
