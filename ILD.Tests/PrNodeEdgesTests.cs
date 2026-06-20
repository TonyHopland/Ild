using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Tests;

public class PrNodeEdgesTests
{
    private static RemotePrSnapshot Snapshot(
        string state = "open",
        bool merged = false,
        bool? mergeable = null,
        string? mergeableState = null,
        RemotePrCiStatus ci = RemotePrCiStatus.None,
        bool approved = false,
        bool changesRequested = false)
        => new(
            "title", "body", state, merged, mergeable, mergeableState, ci, approved, changesRequested,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    [Fact]
    public void ActiveStates_open_pr_maps_each_signal()
    {
        Assert.Contains(PrNodeEdges.OnRejected, PrNodeEdges.ActiveStates(Snapshot(changesRequested: true)));
        Assert.Contains(PrNodeEdges.OnMergeConflict, PrNodeEdges.ActiveStates(Snapshot(mergeable: false)));
        Assert.Contains(PrNodeEdges.OnMergeConflict, PrNodeEdges.ActiveStates(Snapshot(mergeableState: "dirty")));
        Assert.Contains(PrNodeEdges.OnCiFailed, PrNodeEdges.ActiveStates(Snapshot(ci: RemotePrCiStatus.Failed)));
        Assert.Contains(PrNodeEdges.OnApproved, PrNodeEdges.ActiveStates(Snapshot(approved: true)));
        Assert.Contains(PrNodeEdges.OnCiPassed, PrNodeEdges.ActiveStates(Snapshot(ci: RemotePrCiStatus.Passed)));
    }

    [Fact]
    public void ActiveStates_closed_pr_only_surfaces_terminal_state()
    {
        var merged = PrNodeEdges.ActiveStates(Snapshot(state: "closed", merged: true, ci: RemotePrCiStatus.Failed));
        Assert.Equal(new[] { PrNodeEdges.OnMerged }, merged);

        var abandoned = PrNodeEdges.ActiveStates(Snapshot(state: "closed", merged: false, changesRequested: true));
        Assert.Equal(new[] { PrNodeEdges.OnAbandoned }, abandoned);
    }

    [Fact]
    public void HighestPriority_picks_rejected_over_lower_states()
    {
        var candidates = new[] { PrNodeEdges.OnCiPassed, PrNodeEdges.OnRejected, PrNodeEdges.OnApproved };
        Assert.Equal(PrNodeEdges.OnRejected, PrNodeEdges.HighestPriority(candidates));
    }

    [Fact]
    public void HighestPriority_returns_null_for_empty()
        => Assert.Null(PrNodeEdges.HighestPriority(Array.Empty<string>()));

    [Fact]
    public void ParseStates_roundtrips_a_persisted_csv()
    {
        var set = PrNodeEdges.ParseStates($"{PrNodeEdges.OnCiFailed},{PrNodeEdges.OnApproved}");
        Assert.Equal(2, set.Count);
        Assert.Contains(PrNodeEdges.OnCiFailed, set);
        Assert.Contains(PrNodeEdges.OnApproved, set);
        Assert.Empty(PrNodeEdges.ParseStates(null));
    }
}
