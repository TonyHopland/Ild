using ILD.Data;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class PrStatusPollServiceTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private static RemotePrSnapshot Snapshot(
        string state = "open",
        bool merged = false,
        RemotePrCiStatus ci = RemotePrCiStatus.None,
        bool approved = false,
        bool changesRequested = false,
        IReadOnlyList<RemotePrCheck>? failedChecks = null)
        => new("t", "b", state, merged, null, null, ci,
            failedChecks ?? Array.Empty<RemotePrCheck>(), approved, changesRequested,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    private static LoopNodeEdge CustomEdge(Guid sourceNodeId, string name) => new()
    {
        Id = Guid.NewGuid(),
        SourceNodeId = sourceNodeId,
        TargetNodeId = Guid.NewGuid(),
        EdgeType = EdgeType.Custom,
        Name = name,
    };

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public LoopRunNode RunNode { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public Mock<IRunNotifier> Notifier { get; } = new();
        public Mock<IWorkItemManager> WorkItems { get; } = new();
        public Mock<IEventLogService> EventLog { get; } = new();

        public Harness(RemotePrSnapshot snapshot, string? baseline, params LoopNodeEdge[] edges)
        {
            var loopNodeId = Guid.NewGuid();
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-1",
                PrUrl = PrUrl,
                Status = LoopRunStatus.WaitingHuman,
                CurrentNodeId = loopNodeId,
                PrPolledEdgeStates = baseline,
            };
            RunNode = new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = Run.Id,
                LoopNodeId = loopNodeId,
                Status = LoopRunNodeStatus.WaitingHuman,
            };

            Runs.Setup(s => s.GetPrAwaitingMergeRunsAsync()).ReturnsAsync(new[] { Run });
            Runs.Setup(s => s.GetRunNodeAsync(Run.Id, loopNodeId)).ReturnsAsync(RunNode);
            Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(edges);
            Remote.Setup(r => r.GetPullRequestSnapshotAsync("https://github.com/team/repo", "7"))
                .ReturnsAsync(snapshot);
            ClaimsAgainst(Runs, Run);
        }

        public void Returns(RemotePrSnapshot snapshot)
            => Remote.Setup(r => r.GetPullRequestSnapshotAsync("https://github.com/team/repo", "7"))
                .ReturnsAsync(snapshot);

        public PrStatusPollService Build() => new(
            Runs.Object, Remote.Object, Engine.Object, Notifier.Object,
            NullLogger<PrStatusPollService>.Instance, WorkItems.Object, EventLog.Object);
    }

    [Fact]
    public async Task Persists_snapshot_and_pushes_gui_update_every_tick()
    {
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Pending), baseline: null);
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrSnapshot != null)), Times.Once);
        h.Notifier.Verify(n => n.PrSnapshotChangedAsync(h.Run.Id), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task Persisted_snapshot_serializes_ci_as_string_name_not_ordinal()
    {
        // The frontend declares `ci` as a string union and indexes CI_LABELS[ci];
        // a numeric enum ("ci":2) would render a blank CI badge. The persisted
        // blob is round-tripped verbatim into the API response, so it must carry
        // the enum's string name.
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Passed), baseline: null);
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(h.Run.PrSnapshot);
        Assert.Contains("\"ci\":\"Passed\"", h.Run.PrSnapshot);
        Assert.DoesNotContain("\"ci\":2", h.Run.PrSnapshot!);
    }

    [Fact]
    public async Task Fires_connected_edge_on_newly_true_state()
    {
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed), baseline: null);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(h.RunNode.LoopNodeId, LoopOutputs.OnCiFailed) });

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnCiFailed)), Times.Once);
    }

    [Fact]
    public async Task Persisted_snapshot_reads_back_through_the_same_contract_that_wrote_it()
    {
        // The poller writes the blob and the taskboard projection, the webhook
        // resume and the PR node all read it. One owner (PrSnapshotJson) keeps
        // the two sides from drifting — this pins the round-trip.
        var h = new Harness(Snapshot(
            ci: RemotePrCiStatus.Failed,
            failedChecks: new[] { new RemotePrCheck("build", "failure", "https://ci/build", "tsc: 3 errors", "991") }),
            baseline: null);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        var parsed = PrSnapshotJson.TryParse(h.Run.PrSnapshot);
        Assert.Equal(RemotePrCiStatus.Failed, parsed!.Ci);
        Assert.Equal("build", Assert.Single(parsed.FailedChecks).Name);
    }

    [Fact]
    public void A_snapshot_persisted_before_draft_and_merge_blocked_existed_still_reads_back()
    {
        const string legacy = """
            {"title":"t","body":"b","state":"open","merged":false,"mergeable":true,"mergeableState":"clean",
             "ci":"Passed","failedChecks":[],"approved":false,"changesRequested":false,"conversation":[],
             "fetchedAt":"2026-09-01T00:00:00Z"}
            """;

        var parsed = PrSnapshotJson.TryParse(legacy);

        Assert.NotNull(parsed);
        Assert.Equal(RemotePrCiStatus.Passed, parsed!.Ci);
        Assert.False(parsed.Draft);
        Assert.False(parsed.MergeBlocked);
    }

    [Fact]
    public async Task Ci_failed_signal_carries_the_failing_checks_as_the_node_output()
    {
        // The signal's output becomes the resumed PR node's output — i.e. the
        // next node's {{PreviousNode.Output}}. Empty, the fix-it agent can only
        // guess why CI went red.
        var h = new Harness(Snapshot(
            ci: RemotePrCiStatus.Failed,
            failedChecks: new[] { new RemotePrCheck("build", "failure", "https://ci/build", "tsc: 3 errors", "991") }),
            baseline: null);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(h.RunNode.LoopNodeId, LoopOutputs.OnCiFailed) });

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnCiFailed
                && sig.Output != null
                && sig.Output.Contains("build")
                && sig.Output.Contains("https://ci/build")
                && sig.Output.Contains("tsc: 3 errors")
                // …and the way to the log behind the summary, addressed to this run's work item.
                && sig.Output.Contains("check id: 991")
                && sig.Output.Contains("get_ci_log")
                && sig.Output.Contains("wi-1"))), Times.Once);
    }

    [Fact]
    public async Task Every_fired_edge_carries_a_reason_not_an_empty_output()
    {
        var h = new Harness(Snapshot(state: "closed", merged: true), baseline: null);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(h.RunNode.LoopNodeId, LoopOutputs.OnMerged) });

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => !string.IsNullOrWhiteSpace(sig.Output))), Times.Once);
    }

    [Fact]
    public async Task Does_not_fire_when_edge_unconnected_but_still_persists()
    {
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed), baseline: null);
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrPolledEdgeStates!.Contains(LoopOutputs.OnCiFailed))), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task Fires_highest_priority_among_connected_newly_true()
    {
        // Both changes-requested and CI-failed newly true and connected; the
        // higher-priority on_rejected wins.
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed, changesRequested: true), baseline: null);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[]
            {
                CustomEdge(h.RunNode.LoopNodeId, LoopOutputs.OnCiFailed),
                CustomEdge(h.RunNode.LoopNodeId, LoopOutputs.OnRejected),
            });

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnRejected)), Times.Once);
    }

    [Fact]
    public async Task Does_not_refire_a_state_already_in_the_baseline()
    {
        // CI was already failing last tick (in the baseline), so it is not a
        // transition this tick and must not fire again.
        var h = new Harness(Snapshot(ci: RemotePrCiStatus.Failed), baseline: LoopOutputs.OnCiFailed);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new[] { CustomEdge(h.RunNode.LoopNodeId, LoopOutputs.OnCiFailed) });

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    // ── AutoMerge fallback ────────────────────────────────────────────────

    private const string RepoUrl = "https://github.com/team/repo";

    /// <summary>A pull request ILD may merge: open, green, mergeable, not a draft, not blocked.</summary>
    private static RemotePrSnapshot Green(bool approved = false)
        => new RemotePrSnapshot("t", "b", "open", false, true, "clean", RemotePrCiStatus.Passed,
            Array.Empty<RemotePrCheck>(), approved, false,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow) with { Draft = false, MergeBlocked = false };

    private static RemotePrSnapshot MergedByHand()
        => Snapshot(state: "closed", merged: true);

    /// <summary>
    /// The store's compare-and-set, kept on the run instance the poller is handed:
    /// a write lands only when the run still holds what the caller expected.
    /// </summary>
    private static void ClaimsAgainst(Mock<ILoopRunStore> runs, LoopRun run)
        => runs.Setup(s => s.TrySetPrAutoMergeAsync(run.Id, It.IsAny<PrAutoMerge?>(), It.IsAny<PrAutoMerge>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, PrAutoMerge? expected, PrAutoMerge next, string? error) =>
            {
                if (run.PrAutoMerge != expected)
                    return false;
                run.PrAutoMerge = next;
                run.PrAutoMergeError = error;
                return true;
            });

    private static Harness AutoMergeHarness(RemotePrSnapshot snapshot, PrAutoMerge? state, params string[] wired)
    {
        var h = new Harness(snapshot, baseline: null);
        h.Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(wired.Select(name => CustomEdge(h.RunNode.LoopNodeId, name)).ToArray());
        h.Run.PrAutoMerge = state;
        h.Run.HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge;
        h.WorkItems.Setup(m => m.GetWorkItemAsync("wi-1")).ReturnsAsync(new WorkItemView
        {
            Id = "wi-1",
            Title = "T",
            Tags = new[] { "automerge" },
            BranchName = "ild/wi-1-feature",
        });
        return h;
    }

    private static void VerifyMergeCalls(Harness h, Times times)
        => h.Remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), It.IsAny<string>()), times);

    [Fact]
    public async Task AutoMerge_fallback_merges_on_the_first_green_tick_before_any_edge_and_on_merged_fires_next_tick()
    {
        // on_ci_passed and on_approved are wired and both go true on the very
        // tick the merge becomes possible: neither may take the run off the PR
        // node before ILD has merged.
        var h = AutoMergeHarness(Green(approved: true), PrAutoMerge.IldPending,
            LoopOutputs.OnCiPassed, LoopOutputs.OnApproved, LoopOutputs.OnMerged);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7")).ReturnsAsync(true);
        h.Remote.Setup(r => r.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), "7"), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        Assert.Equal(PrAutoMerge.IldMerged, h.Run.PrAutoMerge);
        // The tick still records what it saw.
        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrSnapshot != null)), Times.AtLeastOnce);
        h.EventLog.Verify(e => e.AppendAsync(h.Run.Id, EventType.PrMerged, It.IsAny<string>(),
            It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
        // The same branch the manual merge would delete.
        h.Remote.Verify(r => r.DeleteBranchAsync(It.IsAny<string>(), "ild/wi-1-feature"), Times.Once);

        h.Returns(MergedByHand());
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnMerged)), Times.Once);
        VerifyMergeCalls(h, Times.Once());
        Assert.Equal(LoopRunStatus.WaitingHuman, h.Run.Status);
    }

    [Theory]
    [InlineData(PrAutoMerge.Native)]
    [InlineData(PrAutoMerge.IldMerging)]
    [InlineData(PrAutoMerge.IldMerged)]
    [InlineData(PrAutoMerge.IldFailed)]
    [InlineData(null)]
    public async Task AutoMerge_fallback_never_merges_a_run_that_is_not_pending(PrAutoMerge? state)
    {
        // Native: the provider merges, and ILD must not race it. Null: not an
        // AutoMerge pull request, or a run from before the fallback existed.
        var h = AutoMergeHarness(Green(), state, LoopOutputs.OnCiPassed);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        VerifyMergeCalls(h, Times.Never());
        Assert.Equal(state, h.Run.PrAutoMerge);
    }

    [Theory]
    [InlineData("ci pending")]
    [InlineData("ci failed")]
    [InlineData("no checks")]
    [InlineData("draft")]
    [InlineData("conflicts")]
    [InlineData("dirty")]
    [InlineData("mergeability unknown")]
    [InlineData("blocked")]
    public async Task AutoMerge_fallback_waits_on_a_pull_request_it_may_not_merge_yet_and_merges_once_it_may(string why)
    {
        var green = Green();
        var notYet = why switch
        {
            "ci pending" => green with { Ci = RemotePrCiStatus.Pending },
            "ci failed" => green with { Ci = RemotePrCiStatus.Failed },
            "no checks" => green with { Ci = RemotePrCiStatus.None },
            "draft" => green with { Draft = true },
            "conflicts" => green with { Mergeable = false },
            "dirty" => green with { MergeableState = "dirty" },
            "mergeability unknown" => green with { Mergeable = null },
            // A missing required review or branch protection, as GitHub and Azure DevOps report it.
            "blocked" => green with { MergeBlocked = true },
            _ => throw new ArgumentOutOfRangeException(nameof(why)),
        };
        var h = AutoMergeHarness(notYet, PrAutoMerge.IldPending);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7")).ReturnsAsync(true);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        VerifyMergeCalls(h, Times.Never());
        // Waiting spends nothing: the one attempt is still to come.
        Assert.Equal(PrAutoMerge.IldPending, h.Run.PrAutoMerge);

        h.Returns(green);
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), "7"), Times.Once);
    }

    [Theory]
    [InlineData("tag removed")]
    [InlineData("work item gone")]
    [InlineData("work item unreadable")]
    public async Task AutoMerge_fallback_reads_the_tag_live_and_makes_no_attempt_without_it(string why)
    {
        var h = AutoMergeHarness(Green(), PrAutoMerge.IldPending);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7")).ReturnsAsync(true);
        switch (why)
        {
            case "tag removed":
                h.WorkItems.Setup(m => m.GetWorkItemAsync("wi-1"))
                    .ReturnsAsync(new WorkItemView { Id = "wi-1", Title = "T", Tags = new[] { "backend" } });
                break;
            case "work item gone":
                h.WorkItems.Setup(m => m.GetWorkItemAsync("wi-1")).ReturnsAsync((WorkItemView?)null);
                break;
            default:
                h.WorkItems.Setup(m => m.GetWorkItemAsync("wi-1")).ThrowsAsync(new HttpRequestException("work item server down"));
                break;
        }

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        VerifyMergeCalls(h, Times.Never());
        Assert.Equal(PrAutoMerge.IldPending, h.Run.PrAutoMerge);
        // The rest of the tick is untouched.
        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrSnapshot != null)), Times.Once);

        if (why == "tag removed")
            return;
        // Nothing was attempted, so a later tick that can read the item still merges.
        h.WorkItems.Setup(m => m.GetWorkItemAsync("wi-1"))
            .ReturnsAsync(new WorkItemView { Id = "wi-1", Title = "T", Tags = new[] { "AutoMerge" } });
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);
        h.Remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), "7"), Times.Once);
    }

    [Fact]
    public async Task AutoMerge_fallback_does_not_merge_when_another_writer_holds_the_claim()
    {
        var h = AutoMergeHarness(Green(), PrAutoMerge.IldPending);
        h.Runs.Setup(s => s.TrySetPrAutoMergeAsync(h.Run.Id, It.IsAny<PrAutoMerge?>(), It.IsAny<PrAutoMerge>(), It.IsAny<string?>()))
            .ReturnsAsync(false);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        VerifyMergeCalls(h, Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_AutoMerge_leaves_the_run_parked_asks_a_human_and_is_never_retried(bool throws)
    {
        var h = AutoMergeHarness(Green(), PrAutoMerge.IldPending,
            LoopOutputs.OnCiFailed, LoopOutputs.OnMerged);
        var merge = h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7"));
        if (throws)
            merge.ThrowsAsync(new HttpRequestException("At least 1 approving review is required"));
        else
            merge.ReturnsAsync(false);
        var nodeId = h.Run.CurrentNodeId;
        var failedWhenPushed = false;
        h.Notifier.Setup(n => n.PrSnapshotChangedAsync(h.Run.Id))
            .Callback(() => failedWhenPushed |= h.Run.PrAutoMerge == PrAutoMerge.IldFailed)
            .Returns(Task.CompletedTask);
        string? logged = null;
        h.EventLog.Setup(e => e.AppendAsync(h.Run.Id, EventType.PrMergeFailed, It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .Callback((Guid _, EventType _, string message, Guid? _, Guid? _, string? _) => logged = message)
            .ReturnsAsync(1L);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrAutoMerge.IldFailed, h.Run.PrAutoMerge);
        Assert.False(string.IsNullOrWhiteSpace(h.Run.PrAutoMergeError));
        if (throws)
            Assert.Contains("At least 1 approving review is required", h.Run.PrAutoMergeError);
        // Still parked where it was, waiting on the merge.
        Assert.Equal(LoopRunStatus.WaitingHuman, h.Run.Status);
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, h.Run.HumanFeedbackReason);
        Assert.Equal(nodeId, h.Run.CurrentNodeId);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        // No escalation of any kind: nothing else reached the engine.
        h.Engine.VerifyNoOtherCalls();
        // A human is asked, in the log and in the UI.
        Assert.NotNull(logged);
        Assert.Contains($"AutoMerge: ILD could not merge this PR ({h.Run.PrAutoMergeError}). Please merge it manually.", logged);
        Assert.True(failedWhenPushed);

        // Green again next tick, and on a fresh service: no second attempt.
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);
        VerifyMergeCalls(h, Times.Once());

        // The poller goes on handling the run: CI going red still routes.
        h.Returns(Green() with { Ci = RemotePrCiStatus.Failed });
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);
        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnCiFailed)), Times.Once);

        // …and when the human merges by hand, the loop continues.
        h.Returns(MergedByHand());
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);
        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnMerged)), Times.Once);
        VerifyMergeCalls(h, Times.Once());
    }

    [Fact]
    public async Task A_failed_AutoMerge_tick_still_routes_the_edges_it_would_have_routed_even_when_the_log_is_down()
    {
        var h = AutoMergeHarness(Green(), PrAutoMerge.IldPending, LoopOutputs.OnCiPassed);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7")).ReturnsAsync(false);
        h.EventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<EventType>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("event log unavailable"));

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrAutoMerge.IldFailed, h.Run.PrAutoMerge);
        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnCiPassed)), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_that_will_not_delete_after_an_AutoMerge_is_reported_and_blocks_nothing(bool throws)
    {
        var h = AutoMergeHarness(Green(), PrAutoMerge.IldPending, LoopOutputs.OnCiPassed, LoopOutputs.OnMerged);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7")).ReturnsAsync(true);
        var delete = h.Remote.Setup(r => r.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<string>()));
        if (throws)
            delete.ThrowsAsync(new HttpRequestException("forge down"));
        else
            delete.ReturnsAsync(false);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrAutoMerge.IldMerged, h.Run.PrAutoMerge);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        if (!throws)
            h.EventLog.Verify(e => e.AppendAsync(h.Run.Id, EventType.BranchDeleteFailed, It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);

        h.Returns(MergedByHand());
        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);
        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(sig => sig.EdgeName == LoopOutputs.OnMerged)), Times.Once);
    }

    [Fact]
    public async Task A_merge_that_throws_on_one_run_does_not_stop_the_next_run_merging()
    {
        var h = AutoMergeHarness(Green(), PrAutoMerge.IldPending);
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7"))
            .ThrowsAsync(new TaskCanceledException("forge timed out"));

        var otherNodeId = Guid.NewGuid();
        var other = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-2",
            PrUrl = "https://github.com/team/other/pull/9",
            Status = LoopRunStatus.WaitingHuman,
            HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge,
            CurrentNodeId = otherNodeId,
            PrAutoMerge = PrAutoMerge.IldPending,
        };
        h.Runs.Setup(s => s.GetPrAwaitingMergeRunsAsync()).ReturnsAsync(new[] { h.Run, other });
        h.Runs.Setup(s => s.GetRunNodeAsync(other.Id, otherNodeId)).ReturnsAsync(new LoopRunNode
        {
            Id = Guid.NewGuid(), LoopRunId = other.Id, LoopNodeId = otherNodeId, Status = LoopRunNodeStatus.WaitingHuman,
        });
        ClaimsAgainst(h.Runs, other);
        h.Remote.Setup(r => r.GetPullRequestSnapshotAsync("https://github.com/team/other", "9")).ReturnsAsync(Green());
        h.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "9")).ReturnsAsync(true);
        h.WorkItems.Setup(m => m.GetWorkItemAsync("wi-2"))
            .ReturnsAsync(new WorkItemView { Id = "wi-2", Title = "T2", Tags = new[] { "AutoMerge" } });

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrAutoMerge.IldFailed, h.Run.PrAutoMerge);
        Assert.Contains("forge timed out", h.Run.PrAutoMergeError);
        h.Remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), "9"), Times.Once);
        Assert.Equal(PrAutoMerge.IldMerged, other.PrAutoMerge);
    }
}
