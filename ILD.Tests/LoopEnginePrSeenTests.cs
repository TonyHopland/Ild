using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// What the PR node saw of its pull request becomes the run's stored snapshot,
/// which is the head the heartbeat measures <c>on_new_commits</c> against.
/// </summary>
public class LoopEnginePrSeenTests
{
    [Fact]
    public async Task The_pull_request_the_pr_node_saw_is_stored_as_the_runs_snapshot()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.PR);
        var seen = new RemotePrSnapshot("t", "b", "open", false, null, null, RemotePrCiStatus.None,
            Array.Empty<RemotePrCheck>(), false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow, "abc123");
        h.Registry.Register(new ScriptedExecutor(NodeType.PR,
            new NodeOutcome.NodeStarting(),
            new NodeOutcome.PrSeen(seen),
            new NodeOutcome.PrAttached("https://example.com/o/r/pull/17"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.PrAwaitingMerge, null)));
        h.SeedRun("pr");

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal("https://example.com/o/r/pull/17", run.PrUrl);
        Assert.Equal("abc123", PrSnapshotJson.TryParse(run.PrSnapshot)!.HeadSha);
    }
}
