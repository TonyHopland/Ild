using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The heartbeat is the single place every throttle decision is taken, so this
/// is where "a review landed" becomes (or does not become) a round: one firing
/// per tick carrying the whole batch, nothing for a run that never wired the
/// edge, and nothing for what ILD itself wrote.
/// </summary>
public class PrStatusPollServiceOnCommentTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";

    private static RemotePrSnapshot Snapshot(
        RemotePrCiStatus ci = RemotePrCiStatus.None,
        bool changesRequested = false,
        IReadOnlyList<RemotePrCheck>? failedChecks = null)
        => new("t", "b", "open", false, null, null, ci,
            failedChecks ?? Array.Empty<RemotePrCheck>(), false, changesRequested,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    private static RemotePrReviewItem Inline(
        string id, string path = "src/A.cs", int line = 10, string body = "this allocation is wrong",
        string author = "Copilot", string reviewId = "r1", string? threadId = null, bool resolved = false)
        => new("review", id, threadId ?? $"t{id}", reviewId, path, line, body, author, Head,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), resolved, false);

    private static RemotePrReviewItem Issue(string id, string body, string author = "tony")
        => new("issue", id, null, null, null, null, body, author, Head,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewSummary Review(string id = "r1", bool incomplete = false)
        => new(id, "COMMENTED", "review body", Head,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), "Copilot", incomplete);

    private static RemotePrReviewLedger Ledger(params RemotePrReviewItem[] items)
        => new(new[] { Review() }, items, Head, null);

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
        public List<string?> LedgerWrites { get; } = new();

        public Harness(RemotePrSnapshot snapshot, RemotePrReviewLedger? ledger, string? runLedger, params string[] wiredEdges)
        {
            var loopNodeId = Guid.NewGuid();
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-1",
                PrUrl = PrUrl,
                Status = LoopRunStatus.WaitingHuman,
                HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge,
                CurrentNodeId = loopNodeId,
                PrCommentLedger = runLedger,
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
            Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync(wiredEdges.Select(name => CustomEdge(loopNodeId, name)).ToArray());
            // The ledger is mutated by compare-and-set: every writer reads the
            // column and writes only if it still holds what it read, so the
            // heartbeat cannot revert what landed during its forge fetch.
            Runs.Setup(s => s.GetPrCommentLedgerAsync(Run.Id)).ReturnsAsync(() => Run.PrCommentLedger);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentLedger, StringComparison.Ordinal)) return false;
                    Run.PrCommentLedger = json;
                    LedgerWrites.Add(json);
                    return true;
                });
            Remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(snapshot);
            if (ledger is not null)
                Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(ledger);
        }

        public PrStatusPollService Build() => new(
            Runs.Object, Remote.Object, Engine.Object, Notifier.Object,
            NullLogger<PrStatusPollService>.Instance);

        public NodeSignal? Fired { get; private set; }

        public Harness CaptureSignal()
        {
            Engine.Setup(e => e.SignalNodeResultAsync(Run.Id, RunNode.Id, It.IsAny<NodeSignal>()))
                .Callback<Guid, Guid, NodeSignal>((_, _, signal) => Fired = signal)
                .Returns(Task.CompletedTask);
            return this;
        }
    }

    /// <summary>A run that has been watching this PR, with nothing outstanding.</summary>
    private static string WatchedSince(params RemotePrReviewItem[] alreadySeen)
        => PrCommentLedgerJson.Serialize(
            PrCommentDelivery.Decide(new RemotePrReviewLedger(new[] { Review() }, alreadySeen, Head, null), Head, null).Ledger);

    [Fact]
    public async Task A_comment_that_arrived_since_the_last_tick_fires_on_comment()
    {
        var h = new Harness(Snapshot(), Ledger(Inline("11")), WatchedSince(), PrNodeEdges.OnComment).CaptureSignal();

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(h.Fired);
        Assert.Equal(PrNodeEdges.OnComment, h.Fired!.EdgeName);
    }

    [Fact]
    public async Task The_signal_says_where_each_item_is_who_wrote_it_what_it_says_and_which_commit_it_ran_against()
    {
        var h = new Harness(
            Snapshot(),
            Ledger(
                Inline("4049159495", path: "frontend/src/utils/attachments.ts", line: 30,
                    body: "preserve the original text", threadId: "PRRT-thread-77"),
                Issue("4051372317", "and one at the pull request level", author: "tony")),
            WatchedSince(),
            PrNodeEdges.OnComment).CaptureSignal();

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(h.Fired);
        var output = h.Fired!.Output!;
        Assert.Contains("frontend/src/utils/attachments.ts:30", output, StringComparison.Ordinal);
        Assert.Contains("preserve the original text", output, StringComparison.Ordinal);
        Assert.Contains("Copilot", output, StringComparison.Ordinal);
        Assert.Contains("4049159495", output, StringComparison.Ordinal);
        Assert.Contains("PRRT-thread-77", output, StringComparison.Ordinal);
        Assert.Contains(Head, output, StringComparison.Ordinal);
        // The PR-level one has no file to name, and must say so rather than
        // arriving as a finding about nothing.
        Assert.Contains("and one at the pull request level", output, StringComparison.Ordinal);
        Assert.Contains("4051372317", output, StringComparison.Ordinal);
        Assert.Contains("tony", output, StringComparison.Ordinal);
        Assert.True(
            new[] { "PR-level", "pull-request-level", "pull request comment", "comment on the pull request" }
                .Any(phrase => output.Contains(phrase, StringComparison.OrdinalIgnoreCase)),
            $"an item with no file must say it sits on the pull request itself; got:\n{output}");
        // …and the way to everything that did not fit.
        Assert.Contains("get_pr_review", output, StringComparison.Ordinal);
        Assert.Contains("wi-1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_carrying_many_comments_is_one_firing_not_one_per_comment()
    {
        var h = new Harness(
            Snapshot(),
            Ledger(Inline("11"), Inline("12", path: "src/B.cs"), Inline("13", path: "src/C.cs"), Issue("14", "and a note")),
            WatchedSince(),
            PrNodeEdges.OnComment).CaptureSignal();

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Once);
        Assert.NotNull(h.Fired);
        Assert.Contains("src/B.cs", h.Fired!.Output!, StringComparison.Ordinal);
        Assert.Contains("src/C.cs", h.Fired!.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_items_do_not_fire_again_on_the_next_tick()
    {
        var h = new Harness(Snapshot(), Ledger(Inline("11")), WatchedSince(), PrNodeEdges.OnComment).CaptureSignal();
        var service = h.Build();

        await service.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(h.Fired);

        // What the first tick recorded is what the second starts from: the
        // compare-and-set already left it on the run, as the row would.
        Assert.Equal(h.LedgerWrites.Last(), h.Run.PrCommentLedger);
        h.Engine.Invocations.Clear();

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task A_run_that_has_never_watched_this_pull_request_records_what_is_there_and_fires_nothing()
    {
        var h = new Harness(Snapshot(), Ledger(Inline("11"), Issue("12", "an old note")), runLedger: null, PrNodeEdges.OnComment);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        var seeded = Assert.Single(h.LedgerWrites);
        Assert.NotNull(seeded);
        Assert.Empty(PrCommentDelivery.Decide(
            new RemotePrReviewLedger(new[] { Review() }, new[] { Inline("11"), Issue("12", "an old note") }, Head, null),
            Head, PrCommentLedgerJson.TryParse(seeded)).Items);
    }

    [Fact]
    public async Task With_the_edge_unwired_no_review_ledger_is_fetched_and_none_is_written()
    {
        // Unwired means untouched: no forge call, no persisted ledger, and the
        // snapshot and its GUI push behave exactly as before.
        var h = new Harness(Snapshot(), ledger: null, runLedger: null, PrNodeEdges.OnMerged);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Remote.Verify(r => r.GetPullRequestReviewLedgerAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        h.Runs.Verify(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrSnapshot != null)), Times.Once);
        h.Notifier.Verify(n => n.PrSnapshotChangedAsync(h.Run.Id), Times.Once);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task A_higher_priority_state_wins_the_tick_and_consumes_no_items()
    {
        // The comments are still outstanding after a ci-failed round; marking
        // them delivered here would lose them for good.
        var h = new Harness(
            Snapshot(ci: RemotePrCiStatus.Failed, failedChecks: new[] { new RemotePrCheck("build", "failure", null, null, "991") }),
            Ledger(Inline("11")),
            WatchedSince(),
            PrNodeEdges.OnCiFailed, PrNodeEdges.OnComment).CaptureSignal();

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(h.Fired);
        Assert.Equal(PrNodeEdges.OnCiFailed, h.Fired!.EdgeName);
        h.Runs.Verify(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task A_comment_ild_posted_itself_does_not_fire_the_edge()
    {
        var reply = PrCommentMarker.Stamp("Answered: the build is green.", Guid.NewGuid());
        var h = new Harness(Snapshot(), Ledger(Issue("500", reply)), WatchedSince(), PrNodeEdges.OnComment);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task A_review_that_was_cut_short_does_not_start_a_round()
    {
        var h = new Harness(
            Snapshot(),
            new RemotePrReviewLedger(new[] { Review("r9", incomplete: true) }, new[] { Inline("31", reviewId: "r9") }, Head, null),
            WatchedSince(),
            PrNodeEdges.OnComment);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task The_snapshot_is_still_persisted_and_pushed_on_a_tick_that_fires_a_comment()
    {
        var h = new Harness(Snapshot(), Ledger(Inline("11")), WatchedSince(), PrNodeEdges.OnComment);

        await h.Build().PollOnceAsync(TestContext.Current.CancellationToken);

        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrSnapshot != null)), Times.Once);
        h.Notifier.Verify(n => n.PrSnapshotChangedAsync(h.Run.Id), Times.Once);
    }
}
