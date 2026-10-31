using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A tool run as the agent uid can leave worktree files the orchestrator cannot read, so the
/// engine hands the worktree back to the shared group when an agent-uid node (AI, Cmd) ends,
/// before that outcome is recorded.
/// </summary>
public class LoopEngineWorktreeShareTests
{
    private const string Worktree = "/tmp/ild-share-test-worktree";

    private static NodeOutcome[] Script(string outcome) => outcome switch
    {
        "success" => [new NodeOutcome.NodeStarting("x"), new NodeOutcome.Success(EdgeType.OnSuccess, "ok")],
        "fail" => [new NodeOutcome.NodeStarting("x"), new NodeOutcome.Fail(EdgeType.OnFailure, "boom")],
        "interrupted" => [new NodeOutcome.NodeStarting("x"), new NodeOutcome.Interrupted("limit")],
        "waiting" => [new NodeOutcome.NodeStarting("x"), new NodeOutcome.WaitingAction("ask")],
        "terminal" => [new NodeOutcome.NodeStarting("x"), new NodeOutcome.Terminal("done")],
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static LoopEngineHarness Harness(Mock<IRepositoryManager>? repos, NodeType type, string outcome, string? worktree = Worktree)
    {
        var h = new LoopEngineHarness(configure: repos is null ? null : s => s.AddSingleton(repos.Object));
        h.Registry.Register(new ScriptedExecutor(type, Script(outcome)));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup, new NodeOutcome.NodeStarting("c"), new NodeOutcome.Terminal("done")));
        h.AddNode("n", type);
        h.AddNode("end", NodeType.Cleanup);
        h.AddEdge("n", "end", EdgeType.OnSuccess);
        var run = h.SeedRun("n");
        if (worktree is not null)
        {
            h.Db.Fresh().LoopRuns.Where(r => r.Id == run.Id).ExecuteUpdate(s => s.SetProperty(r => r.WorktreePath, worktree));
            h.Db.Context.Entry(run).Reload();
        }
        return h;
    }

    [Theory]
    [InlineData(NodeType.AI, "success")]
    [InlineData(NodeType.AI, "fail")]
    [InlineData(NodeType.AI, "interrupted")]
    [InlineData(NodeType.AI, "waiting")]
    [InlineData(NodeType.AI, "terminal")]
    [InlineData(NodeType.Cmd, "success")]
    [InlineData(NodeType.Cmd, "fail")]
    public async Task An_agent_node_ending_with_a_worktree_shares_it_before_the_outcome_is_recorded(NodeType type, string outcome)
    {
        var repos = new Mock<IRepositoryManager>();
        using var h = Harness(repos, type, outcome);
        LoopRunNodeStatus? statusWhenShared = null;
        repos.Setup(r => r.ShareWorktreeWithOrchestratorAsync(Worktree, It.IsAny<CancellationToken>()))
            .Callback(() => statusWhenShared = h.ReloadRunNodes().First(n => n.LoopNodeId == h.NodesById["n"].Id).Status)
            .ReturnsAsync(true);

        await h.RunAsync();

        repos.Verify(r => r.ShareWorktreeWithOrchestratorAsync(Worktree, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(LoopRunNodeStatus.Running, statusWhenShared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_throwing_share_never_changes_the_nodes_outcome_or_routing(bool throws)
    {
        var repos = new Mock<IRepositoryManager>();
        var setup = repos.Setup(r => r.ShareWorktreeWithOrchestratorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (throws) setup.ThrowsAsync(new InvalidOperationException("find exploded")); else setup.ReturnsAsync(false);
        using var h = Harness(repos, NodeType.AI, "success");

        await h.RunAsync();

        repos.Verify(r => r.ShareWorktreeWithOrchestratorAsync(Worktree, It.IsAny<CancellationToken>()), Times.Once);
        var nodes = h.ReloadRunNodes();
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes.First(n => n.LoopNodeId == h.NodesById["n"].Id).Status);
        Assert.Contains(nodes, n => n.LoopNodeId == h.NodesById["end"].Id && n.Status == LoopRunNodeStatus.Succeeded);
        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
    }

    [Theory]
    [InlineData(NodeType.Prompt, Worktree)]
    [InlineData(NodeType.Human, Worktree)]
    [InlineData(NodeType.AI, null)]
    public async Task Other_node_types_and_runs_without_a_worktree_are_not_shared(NodeType type, string? worktree)
    {
        var repos = new Mock<IRepositoryManager>();
        using var h = Harness(repos, type, "success", worktree);

        await h.RunAsync();

        repos.Verify(r => r.ShareWorktreeWithOrchestratorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Without_a_repository_manager_registered_the_node_still_completes()
    {
        using var h = Harness(null, NodeType.AI, "success");

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
    }
}
