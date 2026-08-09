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
        bool changesRequested = false,
        IReadOnlyList<RemotePrCheck>? failedChecks = null,
        IReadOnlyList<RemotePrConversationEntry>? conversation = null)
        => new(
            "title", "body", state, merged, mergeable, mergeableState, ci,
            failedChecks ?? Array.Empty<RemotePrCheck>(), approved, changesRequested,
            conversation ?? Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

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

    [Fact]
    public void Describe_ci_failed_names_every_failing_check_with_its_url_and_output()
    {
        var reason = PrNodeEdges.Describe(PrNodeEdges.OnCiFailed, Snapshot(
            ci: RemotePrCiStatus.Failed,
            failedChecks: new[]
            {
                new RemotePrCheck("build", "failure", "https://ci/build", "tsc: 3 errors"),
                new RemotePrCheck("e2e", "timed_out", null, null),
            }));

        Assert.Contains("CI failed", reason);
        Assert.Contains("build", reason);
        Assert.Contains("https://ci/build", reason);
        Assert.Contains("tsc: 3 errors", reason);
        Assert.Contains("e2e", reason);
        Assert.Contains("timed_out", reason);
    }

    [Fact]
    public void Describe_still_says_what_happened_with_no_detail_to_offer()
    {
        // A snapshot persisted before failed checks were captured deserializes
        // them as null; the headline is the floor, never the empty string that
        // left the downstream agent guessing.
        foreach (var edge in PrNodeEdges.ByPriority)
        {
            Assert.NotEmpty(PrNodeEdges.Describe(edge));
            Assert.NotEmpty(PrNodeEdges.Describe(edge, Snapshot(failedChecks: null!)));
        }
        Assert.NotEmpty(PrNodeEdges.Describe(null));
    }

    [Fact]
    public void Describe_rejected_quotes_the_review_that_asked_for_changes()
    {
        var reason = PrNodeEdges.Describe(PrNodeEdges.OnRejected, Snapshot(
            changesRequested: true,
            conversation: new[]
            {
                new RemotePrConversationEntry("review", "alice", "old objection", DateTime.UtcNow.AddDays(-1), "CHANGES_REQUESTED"),
                new RemotePrConversationEntry("review", "bob", "rename the flag", DateTime.UtcNow, "CHANGES_REQUESTED"),
                new RemotePrConversationEntry("review", "carol", "lgtm", DateTime.UtcNow, "APPROVED"),
            }));

        Assert.Contains("bob", reason);
        Assert.Contains("rename the flag", reason);
        Assert.DoesNotContain("old objection", reason);
        Assert.DoesNotContain("lgtm", reason);
    }

    [Fact]
    public void Describe_prefers_the_callers_own_detail_over_the_snapshot()
    {
        var reason = PrNodeEdges.Describe(PrNodeEdges.OnRejected, Snapshot(
                conversation: new[]
                {
                    new RemotePrConversationEntry("review", "alice", "stale snapshot text", DateTime.UtcNow, "CHANGES_REQUESTED"),
                }),
            detail: "the webhook's own comment");

        Assert.Contains("the webhook's own comment", reason);
        Assert.DoesNotContain("stale snapshot text", reason);
    }

    [Fact]
    public void Describe_caps_a_huge_reason()
    {
        var reason = PrNodeEdges.Describe(PrNodeEdges.OnCiFailed, Snapshot(
            ci: RemotePrCiStatus.Failed,
            failedChecks: Enumerable.Range(0, 200)
                .Select(i => new RemotePrCheck($"check-{i}", "failure", null, new string('x', 900)))
                .ToArray()));

        // The cap counts the marker, so it is the length a caller can rely on.
        Assert.True(reason.Length <= PrNodeEdges.MaxReasonLength, $"reason was {reason.Length} chars");
        Assert.Contains("truncated", reason);
        Assert.StartsWith("CI failed", reason);
    }

    [Fact]
    public void Describe_never_cuts_a_surrogate_pair_in_half()
    {
        // CI output carries emoji; half a pair is an unpaired surrogate that
        // survives to the agent's prompt as a replacement character.
        var reason = PrNodeEdges.Describe(PrNodeEdges.OnCiFailed, Snapshot(
            ci: RemotePrCiStatus.Failed,
            failedChecks: new[]
            {
                new RemotePrCheck("build", "failure", null, string.Concat(Enumerable.Repeat("🔥", 6000))),
            }));

        Assert.True(reason.Length <= PrNodeEdges.MaxReasonLength);
        foreach (var (c, i) in reason.Select((c, i) => (c, i)))
            Assert.False(char.IsHighSurrogate(c) && (i + 1 == reason.Length || !char.IsLowSurrogate(reason[i + 1])),
                $"unpaired high surrogate at index {i}");
    }
}
