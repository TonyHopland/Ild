using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// The eighth reserved PR edge and the pointer that makes a batched review
/// readable: the reason text can only carry so much, so what it must never lose
/// is the call that reads the rest.
/// </summary>
public class PrNodeEdgesOnCommentTests
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
    public void On_comment_sits_below_ci_failed_and_above_approved_leaving_the_other_seven_in_order()
    {
        Assert.Equal(
            new[]
            {
                PrNodeEdges.OnRejected,
                PrNodeEdges.OnMergeConflict,
                PrNodeEdges.OnCiFailed,
                PrNodeEdges.OnComment,
                PrNodeEdges.OnApproved,
                PrNodeEdges.OnCiPassed,
                PrNodeEdges.OnMerged,
                PrNodeEdges.OnAbandoned,
            },
            PrNodeEdges.ByPriority);
    }

    [Fact]
    public void The_new_edge_is_named_on_comment()
        => Assert.Equal("on_comment", PrNodeEdges.OnComment);

    [Fact]
    public void A_higher_priority_state_in_the_same_tick_beats_a_comment()
    {
        Assert.Equal(PrNodeEdges.OnCiFailed,
            PrNodeEdges.HighestPriority(new[] { PrNodeEdges.OnComment, PrNodeEdges.OnCiFailed }));
        Assert.Equal(PrNodeEdges.OnComment,
            PrNodeEdges.HighestPriority(new[] { PrNodeEdges.OnApproved, PrNodeEdges.OnComment }));
    }

    [Fact]
    public void A_comment_is_not_a_snapshot_state()
    {
        // on_comment is decided from the per-run delivery ledger, not from the
        // snapshot; deriving it here would fire it off the wrong evidence.
        var everything = PrNodeEdges.ActiveStates(Snapshot(
            ci: RemotePrCiStatus.Failed, approved: true, changesRequested: true, mergeable: false));

        Assert.DoesNotContain(PrNodeEdges.OnComment, everything);
    }

    [Fact]
    public void The_new_edge_still_says_what_happened_with_no_detail_to_offer()
        => Assert.NotEmpty(PrNodeEdges.Describe(PrNodeEdges.OnComment));

    [Fact]
    public void A_comment_batch_ends_with_the_call_that_reads_the_rest()
    {
        var reason = PrNodeEdges.Describe(
            PrNodeEdges.OnComment,
            detail: "### src/A.cs:10 — Copilot\ncomment id: 11\nthis allocation is wrong",
            workItemId: "WI-42");

        Assert.Contains("get_pr_review", reason, StringComparison.Ordinal);
        Assert.Contains("WI-42", reason, StringComparison.Ordinal);
        Assert.True(reason.LastIndexOf("get_pr_review", StringComparison.Ordinal) > reason.IndexOf("src/A.cs", StringComparison.Ordinal),
            "the pointer must come after the items it points past");
    }

    [Fact]
    public void A_batch_several_times_the_cap_comes_back_capped_truncated_in_the_middle_and_still_ending_with_the_call()
    {
        // PR #158's first review alone was eight suppressed findings of several
        // hundred characters each. Appended without a budget, the pointer is the
        // first thing the tail-truncation throws away — leaving a half-list and
        // no way to read the rest.
        var batch = string.Join("\n\n", Enumerable.Range(0, 60).Select(i =>
            $"### src/File{i}.cs:{i} — Copilot\ncomment id: {i}\nthread id: t{i}\n{new string('x', 600)}"));
        Assert.True(batch.Length > PrNodeEdges.MaxReasonLength * 3);

        var reason = PrNodeEdges.Describe(PrNodeEdges.OnComment, detail: batch, workItemId: "WI-42");

        Assert.True(reason.Length <= PrNodeEdges.MaxReasonLength, $"reason was {reason.Length} chars");
        Assert.Contains("truncated", reason, StringComparison.Ordinal);
        Assert.Contains("WI-42", reason, StringComparison.Ordinal);
        Assert.True(
            reason.LastIndexOf("get_pr_review", StringComparison.Ordinal) > reason.IndexOf("truncated", StringComparison.Ordinal),
            "the get_pr_review call must survive the cut, after the truncation marker");
        Assert.True(
            reason.LastIndexOf("get_pr_review", StringComparison.Ordinal) > reason.Length - 400,
            "the call must be at the end of the reason, where the agent reads it");
        Assert.Contains("src/File0.cs", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capped_batch_never_cuts_a_surrogate_pair_in_half()
    {
        // Review bodies carry emoji — the real ones open with one.
        var reason = PrNodeEdges.Describe(
            PrNodeEdges.OnComment,
            detail: string.Concat(Enumerable.Repeat("🟡", 8000)),
            workItemId: "WI-42");

        Assert.True(reason.Length <= PrNodeEdges.MaxReasonLength);
        foreach (var (c, i) in reason.Select((c, i) => (c, i)))
            Assert.False(char.IsHighSurrogate(c) && (i + 1 == reason.Length || !char.IsLowSurrogate(reason[i + 1])),
                $"unpaired high surrogate at index {i}");
    }

    [Fact]
    public void A_rejected_review_now_also_names_the_call_that_reads_its_comments()
    {
        // While changes are requested, on_rejected outranks on_comment every
        // tick, so that round only ever sees the review's prose — the comments
        // behind it are reachable only through the tool.
        var reason = PrNodeEdges.Describe(PrNodeEdges.OnRejected, Snapshot(
            changesRequested: true,
            conversation: new[]
            {
                new RemotePrConversationEntry("review", "alice", "old objection", DateTime.UtcNow.AddDays(-1), "CHANGES_REQUESTED"),
                new RemotePrConversationEntry("review", "bob", "rename the flag", DateTime.UtcNow, "CHANGES_REQUESTED"),
            }),
            workItemId: "WI-42");

        Assert.Contains("bob", reason, StringComparison.Ordinal);
        Assert.Contains("rename the flag", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("old objection", reason, StringComparison.Ordinal);
        Assert.True(
            reason.LastIndexOf("get_pr_review", StringComparison.Ordinal) > reason.IndexOf("rename the flag", StringComparison.Ordinal),
            "the pointer is appended after the review it already quotes");
        Assert.Contains("WI-42", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pointer_is_only_offered_where_it_can_be_called()
    {
        // No work item id to spell into the call means no call to advertise,
        // and the other six edges' reasons are untouched.
        Assert.DoesNotContain("get_pr_review",
            PrNodeEdges.Describe(PrNodeEdges.OnComment, detail: "a comment"), StringComparison.Ordinal);

        Assert.DoesNotContain("get_pr_review", PrNodeEdges.Describe(PrNodeEdges.OnCiFailed, Snapshot(
                ci: RemotePrCiStatus.Failed,
                failedChecks: new[] { new RemotePrCheck("build", "failure", "https://ci/build", "tsc: 3 errors", "991") }),
            workItemId: "WI-42"), StringComparison.Ordinal);

        foreach (var edge in new[] { PrNodeEdges.OnMergeConflict, PrNodeEdges.OnApproved, PrNodeEdges.OnCiPassed, PrNodeEdges.OnMerged, PrNodeEdges.OnAbandoned })
            Assert.DoesNotContain("get_pr_review", PrNodeEdges.Describe(edge, Snapshot(), workItemId: "WI-42"), StringComparison.Ordinal);
    }
}
