using ILD.Data.DTOs;

namespace ILD.Core.Services.Remote;

/// <summary>
/// The seven reserved custom-edge names a PR node may declare, the state each
/// fires on, and the priority used to pick a single edge when several states
/// newly become true in one heartbeat tick. The PR heartbeat poller emits a
/// <c>NodeSignal.Custom</c> for the highest-priority edge that is both
/// newly-true and actually connected; everything else only updates the
/// persisted snapshot. See the PR Node entry in CONTEXT.md.
/// </summary>
public static class PrNodeEdges
{
    public const string OnRejected = "on_rejected";
    public const string OnMergeConflict = "on_merge_conflict";
    public const string OnCiFailed = "on_ci_failed";
    public const string OnApproved = "on_approved";
    public const string OnCiPassed = "on_ci_passed";
    public const string OnMerged = "on_merged";
    public const string OnAbandoned = "on_abandoned";

    /// <summary>Reserved edge names in descending priority (index 0 = highest).</summary>
    public static readonly IReadOnlyList<string> ByPriority = new[]
    {
        OnRejected,
        OnMergeConflict,
        OnCiFailed,
        OnApproved,
        OnCiPassed,
        OnMerged,
        OnAbandoned,
    };

    /// <summary>
    /// The set of edge-state names that are currently true for a snapshot. A
    /// closed PR surfaces only its terminal state (<c>on_merged</c> /
    /// <c>on_abandoned</c>); an open PR surfaces the review/conflict/CI states.
    /// </summary>
    public static HashSet<string> ActiveStates(RemotePrSnapshot s)
    {
        var states = new HashSet<string>(StringComparer.Ordinal);
        if (string.Equals(s.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            states.Add(s.Merged ? OnMerged : OnAbandoned);
            return states;
        }

        if (s.ChangesRequested) states.Add(OnRejected);
        if (s.Mergeable == false || string.Equals(s.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase))
            states.Add(OnMergeConflict);
        if (s.Ci == RemotePrCiStatus.Failed) states.Add(OnCiFailed);
        if (s.Approved) states.Add(OnApproved);
        if (s.Ci == RemotePrCiStatus.Passed) states.Add(OnCiPassed);
        return states;
    }

    /// <summary>The highest-priority edge name in <paramref name="candidates"/>, or null when empty.</summary>
    public static string? HighestPriority(IEnumerable<string> candidates)
    {
        var set = candidates as ISet<string> ?? new HashSet<string>(candidates, StringComparer.Ordinal);
        return ByPriority.FirstOrDefault(set.Contains);
    }

    /// <summary>Parse the comma-separated persisted baseline back into a set.</summary>
    public static HashSet<string> ParseStates(string? csv)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(csv)) return set;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);
        return set;
    }
}
