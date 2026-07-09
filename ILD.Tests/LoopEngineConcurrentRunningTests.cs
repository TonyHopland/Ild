using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// Regression tests for issue #39: a loop run must never have more than one
/// AI node marked <see cref="LoopRunNodeStatus.Running"/> at a time. A re-drive
/// (retry, halt-resume, signal, crash recovery) that starts a fresh node while
/// a previous driver's node is still Running would otherwise leave two nodes
/// Running concurrently — the invalid state the run visualization showed.
/// </summary>
public class LoopEngineConcurrentRunningTests
{
    [Fact]
    public async Task Redrive_interrupts_a_stale_running_node_so_at_most_one_node_is_Running()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI);
        h.AddNode("b", NodeType.AI);

        // Re-driving enters at node "b"; node "b" starts and stays Running (the
        // executor yields NodeStarting and no terminal outcome, so the node is
        // left in-flight when the drive parks).
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("b")));

        var run = h.SeedRun("b");

        // A previous (dead) driver left node "a" marked Running — e.g. a crash
        // that finalized the run but never cleared its in-flight node. Without
        // the fix this row survives the re-drive and coexists with node "b".
        var stale = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = h.NodesById["a"].Id,
            NodeLabel = "a",
            Status = LoopRunNodeStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        h.Db.Context.LoopRunNodes.Add(stale);
        h.Db.Context.SaveChanges();

        await h.RunAsync();

        var nodes = h.ReloadRunNodes();
        Assert.Equal(1, nodes.Count(n => n.Status == LoopRunNodeStatus.Running));

        var staleReloaded = nodes.Single(n => n.Id == stale.Id);
        Assert.Equal(LoopRunNodeStatus.Interrupted, staleReloaded.Status);
        Assert.NotNull(staleReloaded.CompletedAt);

        // The one Running node is the freshly started node "b", not the stale one.
        var running = nodes.Single(n => n.Status == LoopRunNodeStatus.Running);
        Assert.Equal(h.NodesById["b"].Id, running.LoopNodeId);
    }

    [Fact]
    public async Task Clean_start_leaves_no_extra_interruptions()
    {
        // The interrupt sweep at drive start is a no-op on the happy path: a run
        // that never left a node Running is driven to completion unchanged.
        using var h = new LoopEngineHarness();
        h.AddNode("s", NodeType.Start);
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("s", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        var nodes = h.ReloadRunNodes();
        Assert.All(nodes, n => Assert.Equal(LoopRunNodeStatus.Succeeded, n.Status));
        Assert.DoesNotContain(nodes, n => n.Status == LoopRunNodeStatus.Interrupted);
    }
}
