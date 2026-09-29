using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// An edge's UserVisible decides only which buttons the run UI offers while a
/// run is parked at the edge's source node — the actions the park hands the
/// work item. The system still fires a hidden edge exactly as before.
/// </summary>
public class LoopEngineUserVisibleEdgeTests
{
    private static void Hide(LoopEngineHarness h, string from, EdgeType type, string? name = null)
    {
        var sourceId = h.NodesById[from].Id;
        var edge = h.Db.Context.LoopNodeEdges.Single(e => e.SourceNodeId == sourceId && e.EdgeType == type && e.Name == name);
        edge.UserVisible = false;
        h.Db.Context.SaveChanges();
    }

    /// <summary>The actions the park sent with the work item's move to HumanFeedback.</summary>
    private static string? ParkedActions(LoopEngineHarness h)
    {
        var park = h.WorkItemsMock.Invocations.Single(i =>
            i.Method.Name == nameof(IWorkItemManager.TransitionAsync)
            && (RemoteWorkItemStatus)i.Arguments[1] == RemoteWorkItemStatus.HumanFeedback);
        return (string?)park.Arguments[3];
    }

    private static async Task ParkAt(LoopEngineHarness h, string key, NodeType type, string reason)
    {
        h.Registry.Register(new ScriptedExecutor(type,
            new NodeOutcome.NodeStarting("park"),
            new NodeOutcome.WaitingAction(reason, "prompt")));
        h.SeedRun(key);
        await h.RunAsync();
        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);
    }

    [Fact]
    public async Task A_parked_node_offers_only_its_visible_edges()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);
        h.AddNode("next", NodeType.Cmd);
        h.AddEdge("h", "next", EdgeType.OnSuccess);
        h.AddEdge("h", "next", EdgeType.OnFailure);
        h.AddEdge("h", "next", EdgeType.Custom, "Respond");
        h.AddEdge("h", "next", EdgeType.Custom, "Escalate");
        Hide(h, "h", EdgeType.OnFailure);
        Hide(h, "h", EdgeType.Custom, "Escalate");

        await ParkAt(h, "h", NodeType.Human, "Awaiting input");

        var offered = ParkedActions(h)!.Split(',', StringSplitOptions.TrimEntries).ToHashSet();
        Assert.Equal(new HashSet<string> { "OnSuccess", "Respond" }, offered);
    }

    [Fact]
    public async Task A_parked_node_whose_edges_are_all_hidden_offers_an_empty_action_list()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.PR);
        h.AddNode("fix", NodeType.Cmd);
        h.AddNode("done", NodeType.Cleanup);
        h.AddEdge("pr", "fix", EdgeType.Custom, PrNodeEdges.OnCiFailed);
        h.AddEdge("pr", "done", EdgeType.Custom, PrNodeEdges.OnMerged);
        Hide(h, "pr", EdgeType.Custom, PrNodeEdges.OnCiFailed);
        Hide(h, "pr", EdgeType.Custom, PrNodeEdges.OnMerged);

        await ParkAt(h, "pr", NodeType.PR, HumanFeedbackReasons.PrAwaitingMerge);

        // Empty, not absent: absent means "no edges" and brings back the default buttons.
        Assert.Equal(string.Empty, ParkedActions(h));
    }

    [Fact]
    public async Task A_parked_node_without_outgoing_edges_offers_no_action_list()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);

        await ParkAt(h, "h", NodeType.Human, "Awaiting input");

        Assert.Null(ParkedActions(h));
    }

    [Fact]
    public async Task A_hidden_custom_edge_still_routes_when_a_person_picks_it_through_the_api()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);
        h.AddNode("after", NodeType.Cmd);
        h.AddEdge("h", "after", EdgeType.Custom, "Escalate");
        Hide(h, "h", EdgeType.Custom, "Escalate");

        var humanExec = new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt"));
        humanExec.Then(
            new NodeOutcome.NodeStarting("re-entry"),
            new NodeOutcome.Success(EdgeType.Custom, "escalated", "Escalate"));
        h.Registry.Register(humanExec);
        var after = new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("after"),
            new NodeOutcome.Terminal("done"));
        h.Registry.Register(after);

        h.SeedRun("h");
        await h.RunAsync();
        var waiting = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);

        await h.Engine.SignalNodeResultAsync(h.RunId, waiting.Id, NodeSignal.Custom("Escalate", "why"));
        await h.WaitUntilIdleAsync();

        Assert.Equal(1, after.Invocations);
    }

    [Fact]
    public async Task A_hidden_reserved_pr_edge_still_routes_when_the_pr_heartbeat_fires_it()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.PR);
        h.AddNode("coder", NodeType.Cmd);
        h.AddEdge("pr", "coder", EdgeType.Custom, PrNodeEdges.OnCiFailed);
        Hide(h, "pr", EdgeType.Custom, PrNodeEdges.OnCiFailed);

        var prExec = new ScriptedExecutor(NodeType.PR,
            new NodeOutcome.NodeStarting("open pr"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.PrAwaitingMerge, "prompt"));
        prExec.Then(
            new NodeOutcome.NodeStarting("re-entry"),
            new NodeOutcome.Success(EdgeType.Custom, "ci-failed", PrNodeEdges.OnCiFailed));
        h.Registry.Register(prExec);
        var coder = new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("coder"),
            new NodeOutcome.Terminal("done"));
        h.Registry.Register(coder);

        h.SeedRun("pr");
        await h.RunAsync();
        var tracked = h.Db.Context.LoopRuns.First(r => r.Id == h.RunId);
        tracked.PrUrl = "https://github.com/team/repo/pull/7";
        h.Db.Context.SaveChanges();

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestSnapshotAsync("https://github.com/team/repo", "7"))
            .ReturnsAsync(new RemotePrSnapshot("t", "b", "open", false, null, null, RemotePrCiStatus.Failed,
                new[] { new RemotePrCheck("build", "failure", "https://ci.example.com/1", "3 errors", "991") },
                false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow));
        var poller = new PrStatusPollService(
            h.Db.LoopRuns, remote.Object, h.Engine, new Mock<IRunNotifier>().Object,
            NullLogger<PrStatusPollService>.Instance);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        await h.WaitUntilIdleAsync();

        Assert.Equal(1, coder.Invocations);
    }
}
