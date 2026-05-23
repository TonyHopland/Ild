using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class EngineSignalValidationTests
{
    [Fact]
    public async Task SignalNodeResultAsync_validates_runNodeId_belongs_to_run()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);

        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt")));

        h.SeedRun("h");
        await h.RunAsync();

        var waitingNode = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);

        // Signal with a fake runNodeId that doesn't belong to this run.
        var fakeNodeId = Guid.NewGuid();
        await h.Engine.SignalNodeResultAsync(h.RunId, fakeNodeId,
            new NodeSignal(ExternalActionResultType.Success, Output: "user-text"));

        // The signal should be rejected: run stays WaitingHuman.
        await Task.Delay(200);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
    }

    [Fact]
    public async Task SignalNodeResultAsync_validates_runNodeId_is_WaitingHuman()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);
        h.AddNode("c", NodeType.Cmd);

        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt")));

        h.SeedRun("h");
        await h.RunAsync();

        // Create a succeeded run node for a different node.
        var succeededNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = h.RunId,
            LoopNodeId = h.NodesById["c"].Id,
            NodeLabel = "c",
            Status = LoopRunNodeStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        h.Db.Context.LoopRunNodes.Add(succeededNode);
        h.Db.Context.SaveChanges();

        // Signal with the succeeded node's ID — should be rejected.
        await h.Engine.SignalNodeResultAsync(h.RunId, succeededNode.Id,
            new NodeSignal(ExternalActionResultType.Success, Output: "user-text"));

        await Task.Delay(200);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
    }

    [Fact]
    public async Task SignalNodeResultAsync_accepts_valid_WaitingHuman_runNodeId()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);
        h.AddNode("after", NodeType.Cmd);
        h.AddEdge("h", "after", EdgeType.OnRespond);

        var humanExec = new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt"));
        humanExec.Then(
            new NodeOutcome.NodeStarting("re-entry"),
            new NodeOutcome.Success(EdgeType.OnRespond, "human-said-yes"));
        h.Registry.Register(humanExec);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("after"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("h");
        await h.RunAsync();

        var waitingNode = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);

        // Signal with the correct WaitingHuman node ID — should succeed.
        await h.Engine.SignalNodeResultAsync(h.RunId, waitingNode.Id,
            new NodeSignal(ExternalActionResultType.Respond, Output: "user-text"));

        await WaitUntilAsync(() => h.ReloadRun().Status != LoopRunStatus.Running, TimeSpan.FromSeconds(5));

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (predicate()) return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Shared in-memory SQLite connection may be mid-flight.
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Predicate did not become true within timeout");
    }
}
