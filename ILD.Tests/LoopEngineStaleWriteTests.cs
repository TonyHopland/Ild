using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// The engine holds its LoopRun instance across an entire node execution
/// (potentially hours for an AI node) and persists with full-entity writes.
/// These tests pin the reload guard: control-plane changes made by other
/// scopes while the node runs (cancel, retain-pin) must survive the engine's
/// completion write instead of being silently reverted.
/// </summary>
public class LoopEngineStaleWriteTests
{
    [Fact]
    public async Task Cancel_during_node_execution_is_not_clobbered_by_node_completion()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("b", NodeType.Prompt);
        h.AddEdge("a", "b", EdgeType.OnSuccess);
        var run = h.SeedRun("a");

        // While node A executes, a separate scope cancels the run (the same
        // write CancelRunAsync performs).
        h.Registry.Register(new MutatingExecutor(NodeType.Cmd, () =>
        {
            using var fresh = h.Db.Fresh();
            var r = fresh.LoopRuns.First(x => x.Id == run.Id);
            r.Status = LoopRunStatus.Cancelled;
            r.CompletedAt = DateTime.UtcNow;
            fresh.SaveChanges();
        }));
        h.Registry.Register(new ScriptedExecutor(NodeType.Prompt,
            new NodeOutcome.NodeStarting("b"), new NodeOutcome.Success(EdgeType.OnSuccess, "b done")));

        await h.RunAsync();

        var after = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Cancelled, after.Status);
        // The engine must not have routed past the cancelled node into B.
        var nodes = h.ReloadRunNodes();
        Assert.Single(nodes);
        // The node's own result is still recorded — the work did happen.
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes[0].Status);
    }

    [Fact]
    public async Task Retain_pin_during_node_execution_survives_node_completion()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        var run = h.SeedRun("a");

        // User pins the run (PUT /loopruns/{id}/retain) while the node runs.
        h.Registry.Register(new MutatingExecutor(NodeType.Cmd, () =>
        {
            using var fresh = h.Db.Fresh();
            var r = fresh.LoopRuns.First(x => x.Id == run.Id);
            r.Retain = true;
            fresh.SaveChanges();
        }));

        await h.RunAsync();

        var after = h.ReloadRun();
        // No outgoing edge: the run completed — but the pin must survive,
        // otherwise the retention sweeper later deletes a pinned run.
        Assert.Equal(LoopRunStatus.Completed, after.Status);
        Assert.True(after.Retain);
    }

    [Fact]
    public async Task Pause_during_node_execution_parks_after_the_node_completes()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("b", NodeType.Prompt);
        h.AddEdge("a", "b", EdgeType.OnSuccess);
        var run = h.SeedRun("a");

        h.Registry.Register(new MutatingExecutor(NodeType.Cmd, () =>
        {
            using var fresh = h.Db.Fresh();
            var r = fresh.LoopRuns.First(x => x.Id == run.Id);
            r.IsPaused = true;
            fresh.SaveChanges();
        }));
        h.Registry.Register(new ScriptedExecutor(NodeType.Prompt,
            new NodeOutcome.NodeStarting("b"), new NodeOutcome.Success(EdgeType.OnSuccess, "b done")));

        await h.RunAsync();

        var after = h.ReloadRun();
        // The flag wasn't clobbered, and the loop parked at the node boundary
        // instead of running B.
        Assert.True(after.IsPaused);
        Assert.Equal(LoopRunStatus.Running, after.Status);
        Assert.Single(h.ReloadRunNodes());
    }

    /// <summary>Yields NodeStarting, fires <paramref name="betweenYields"/> once
    /// (simulating a concurrent writer in another scope), then yields Success.</summary>
    private sealed class MutatingExecutor : INodeExecutor
    {
        public NodeType NodeType { get; }
        private readonly Action _betweenYields;
        private bool _fired;

        public MutatingExecutor(NodeType type, Action betweenYields)
        {
            NodeType = type;
            _betweenYields = betweenYields;
        }

        public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
        {
            yield return new NodeOutcome.NodeStarting("start");
            if (!_fired)
            {
                _fired = true;
                _betweenYields();
            }
            await Task.Yield();
            yield return new NodeOutcome.Success(EdgeType.OnSuccess, "done");
        }
    }
}
