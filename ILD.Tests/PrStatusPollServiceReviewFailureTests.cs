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
/// What the heartbeat does when the forge will not say what is on the pull
/// request. Nothing was read, so there is nothing to record — and a run with no
/// ledger yet must NOT come out of the tick believing it has watched an empty
/// pull request, because the next tick would then deliver the PR's whole history
/// in one firing with no comment of ILD's own anywhere in it for the marker to
/// catch.
/// </summary>
public class PrStatusPollServiceReviewFailureTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";

    private static RemotePrSnapshot Snapshot()
        => new("t", "b", "open", false, null, null, RemotePrCiStatus.None,
            Array.Empty<RemotePrCheck>(), false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    private static RemotePrReviewItem Inline(string id)
        => new("review", id, $"t{id}", "r1", "src/A.cs", 10, "this allocation is wrong", "Copilot", Head,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Readable(params RemotePrReviewItem[] items)
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", "body", Head, DateTime.UtcNow, "Copilot", false) },
            items, Head, null);

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public Mock<IRunNotifier> Notifier { get; } = new();

        public Harness(RemotePrReviewLedger ledger, string? runLedger)
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
            var runNode = new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = Run.Id,
                LoopNodeId = loopNodeId,
                Status = LoopRunNodeStatus.WaitingHuman,
            };

            Runs.Setup(s => s.GetPrAwaitingMergeRunsAsync()).ReturnsAsync(new[] { Run });
            // Compare-and-set, as every writer of this column now is.
            Runs.Setup(s => s.GetPrCommentLedgerAsync(It.IsAny<Guid>())).ReturnsAsync(() => Run.PrCommentLedger);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentLedger, StringComparison.Ordinal)) return false;
                    Run.PrCommentLedger = json;
                    return true;
                });
            Runs.Setup(s => s.GetRunNodeAsync(Run.Id, loopNodeId)).ReturnsAsync(runNode);
            Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new[]
            {
                new LoopNodeEdge
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = loopNodeId,
                    TargetNodeId = Guid.NewGuid(),
                    EdgeType = EdgeType.Custom,
                    Name = PrNodeEdges.OnComment,
                },
            });
            Remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Snapshot());
            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(ledger);
        }

        public Task PollAsync() => new PrStatusPollService(
            Runs.Object, Remote.Object, Engine.Object, Notifier.Object,
            NullLogger<PrStatusPollService>.Instance).PollOnceAsync();
    }

    [Fact]
    public async Task A_forge_that_could_not_be_read_records_nothing_and_fires_nothing()
    {
        var h = new Harness(RemotePrReviewLedger.Unavailable("The provider request failed."), runLedger: null);

        await h.PollAsync();

        h.Runs.Verify(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        Assert.Null(h.Run.PrCommentLedger);
    }

    [Fact]
    public async Task A_failed_read_still_persists_the_snapshot_and_pushes_it()
    {
        var h = new Harness(RemotePrReviewLedger.Unavailable("The provider request failed."), runLedger: null);

        await h.PollAsync();

        h.Runs.Verify(s => s.UpdateRunAsync(It.Is<LoopRun>(r => r.PrSnapshot != null)), Times.Once);
        h.Notifier.Verify(n => n.PrSnapshotChangedAsync(h.Run.Id), Times.Once);
    }

    [Fact]
    public async Task A_tick_that_failed_leaves_the_next_one_still_on_its_first_watch()
    {
        // The whole point: the failed tick must not have claimed the run had
        // seen an empty pull request.
        var failed = new Harness(RemotePrReviewLedger.Unavailable("The provider request failed."), runLedger: null);
        await failed.PollAsync();

        var recovered = new Harness(Readable(Inline("11")), runLedger: failed.Run.PrCommentLedger);
        await recovered.PollAsync();

        recovered.Engine.Verify(
            e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        recovered.Runs.Verify(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task An_unreadable_ledger_blob_on_the_run_is_treated_as_a_first_watch_not_as_an_empty_one()
    {
        // A ledger this version cannot parse is the same situation as never
        // having watched: record what is there, fire nothing.
        var h = new Harness(Readable(Inline("11")), runLedger: "{not json");

        await h.PollAsync();

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        Assert.Empty(PrCommentDelivery.Decide(
            Readable(Inline("11")), Head, PrCommentLedgerJson.TryParse(h.Run.PrCommentLedger)).Items);
    }
}
