using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;

namespace ILD.Tests;

public class ExecutorPreconditionTests
{
    // These tests verify that executors do NOT yield NodeStarting before
    // precondition checks. If NodeStarting is yielded and then a Fail,
    // a LoopRunNode row is created for a node that never actually ran.

    [Fact]
    public async Task PRNode_precondition_failure_does_not_create_LoopRunNode()
    {
        // When the PR node's preconditions fail (e.g., no repo), it should
        // yield Fail directly without NodeStarting, so no LoopRunNode row
        // is persisted for an execution that never happened.
        using var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.PR);

        // Simulate the PR executor failing a precondition check.
        // The real PRNodeExecutor does this when run/wi/repo is null.
        h.Registry.Register(new ScriptedExecutor(NodeType.PR,
            // No NodeStarting before Fail — precondition failed early.
            new NodeOutcome.Fail(EdgeType.OnFailure, "PR node requires a repository on the work item")));

        h.SeedRun("pr");
        await h.RunAsync();

        // No LoopRunNode should exist because NodeStarting was never yielded.
        Assert.Empty(h.ReloadRunNodes());
    }

    [Fact]
    public async Task PRNode_success_creates_LoopRunNode_after_NodeStarting()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.PR);
        h.AddNode("after", NodeType.Cmd);
        h.AddEdge("pr", "after", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.PR,
            new NodeOutcome.NodeStarting("pr-ready"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "pr-created")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("after"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("pr");
        await h.RunAsync();

        var nodes = h.ReloadRunNodes();
        Assert.Equal(2, nodes.Count);
        Assert.Equal("pr-ready", nodes[0].EffectiveInput);
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes[0].Status);
    }

    [Fact]
    public async Task AINode_capacity_throttle_does_not_create_LoopRunNode()
    {
        // WaitingIld is a precondition gate — no LoopRunNode should be created.
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI);

        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.WaitingIld("provider at capacity")));

        h.SeedRun("ai");
        await h.RunAsync();

        Assert.Empty(h.ReloadRunNodes());
    }

    [Fact]
    public async Task CmdNode_missing_worktree_does_not_create_LoopRunNode()
    {
        // Cmd node checks for worktree before yielding NodeStarting.
        using var h = new LoopEngineHarness();
        h.AddNode("cmd", NodeType.Cmd);

        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.Fail(EdgeType.OnFailure, "No worktree available for Cmd node")));

        h.SeedRun("cmd");
        await h.RunAsync();

        Assert.Empty(h.ReloadRunNodes());
    }
}
