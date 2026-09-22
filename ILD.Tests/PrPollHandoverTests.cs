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
/// What the signal says and what the ledger records have to be the same batch.
///
/// The pass decides once before going to the forge — seconds — and the write
/// decides again against the row as it stands, precisely so a drop landing in
/// that window is not reverted. Those two answers differ exactly when the window
/// mattered, and the signal was built from the older one: the write would record
/// a finding as delivered while the agent was never told it existed, and on that
/// head it never fires again. The batch that was WRITTEN is the one that was
/// handed over, so it is the one the round gets told about.
/// </summary>
public class PrPollHandoverTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";
    private const string NewFinding = "this allocation is wrong";
    private const string DroppedFinding = "and this name is misleading";

    private static RemotePrSnapshot Snapshot()
        => new("t", "b", "open", false, null, null, RemotePrCiStatus.None,
            Array.Empty<RemotePrCheck>(), false, false,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    private static RemotePrReviewItem Inline(string id, string body)
        => new("review", id, $"t{id}", "r1", "src/A.cs", int.Parse(id), body, "Copilot", Head,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Fetched(params RemotePrReviewItem[] items)
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", null, Head,
                new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), "Copilot", false) },
            items, Head, null);

    /// <summary>A ledger that has already handed over every one of <paramref name="delivered"/>.</summary>
    private static string Holding(params RemotePrReviewItem[] delivered)
        => PrCommentLedgerJson.Serialize(PrCommentLedger.Empty with
        {
            Head = Head,
            DeliveredIds = delivered.Select(i => PrCommentLedger.KeyFor(i.Kind, i.CommentId!)).ToList(),
            DeliveredHashes = delivered.Select(i => PrCommentLedger.Fingerprint(i.Path, i.Line, i.Body)).ToList(),
        });

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public LoopRunNode RunNode { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();
        public NodeSignal? Fired { get; private set; }

        /// <summary>The persisted column — deliberately NOT the copy the pass carries.</summary>
        public string? Row { get; set; }

        public bool WritesLand { get; set; } = true;

        public Harness(RemotePrReviewLedger fetched, string? carried, string? row)
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
                // What the pass loaded before it went to the forge.
                PrCommentLedger = carried,
            };
            Row = row;
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
                .ReturnsAsync(new[]
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
            Runs.Setup(s => s.GetPrCommentLedgerAsync(Run.Id)).ReturnsAsync(() => Row);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!WritesLand || !string.Equals(expected, Row, StringComparison.Ordinal)) return false;
                    Row = json;
                    return true;
                });

            Remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Snapshot());
            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(fetched);
            Engine.Setup(e => e.SignalNodeResultAsync(Run.Id, RunNode.Id, It.IsAny<NodeSignal>()))
                .Callback<Guid, Guid, NodeSignal>((_, _, signal) => Fired = signal)
                .Returns(Task.CompletedTask);
        }

        public Task PollAsync() => new PrStatusPollService(
            Runs.Object, Remote.Object, Engine.Object, Mock.Of<IRunNotifier>(),
            NullLogger<PrStatusPollService>.Instance).PollOnceAsync();
    }

    [Fact]
    public async Task A_finding_put_back_inside_the_window_is_in_the_signal_and_not_only_in_the_ledger()
    {
        // The reviewer's own example. The pass sees one new finding; while it is
        // at the forge, a human drops the answer to an older one, which puts that
        // finding back. The write hands over both — so both have to be described.
        var incoming = Inline("10", NewFinding);
        var putBack = Inline("11", DroppedFinding);
        var h = new Harness(Fetched(incoming, putBack), carried: Holding(putBack), row: Holding());

        await h.PollAsync();

        Assert.NotNull(h.Fired);
        Assert.Contains(NewFinding, h.Fired!.Output, StringComparison.Ordinal);
        Assert.Contains(DroppedFinding, h.Fired.Output, StringComparison.Ordinal);
        var recorded = PrCommentLedgerJson.TryParse(h.Row);
        Assert.Contains(PrCommentLedger.KeyFor("review", "11"), recorded!.DeliveredIds);
    }

    [Fact]
    public async Task A_batch_somebody_else_took_inside_the_window_resumes_nothing()
    {
        // The other direction: by the time the write ran there was nothing left
        // to hand over. Resuming anyway starts a round with an empty batch and a
        // reason that names findings it will not be given.
        var incoming = Inline("10", NewFinding);
        var h = new Harness(Fetched(incoming), carried: Holding(), row: Holding(incoming));

        await h.PollAsync();

        Assert.Null(h.Fired);
    }

    [Fact]
    public async Task A_handover_that_could_not_be_recorded_still_fires_on_what_this_pass_decided()
    {
        // Pinning what this round did NOT change. Nothing was written, so the
        // effective batch is unknowable and the tick falls back to the decision
        // it already had — which is how it behaved before the write had an
        // answer to give. Blocking here instead would be safer against a
        // repeated round and worse against a store that stays broken: the edge
        // would go quiet for good, with nothing saying so. That trade is open
        // in the escalation, not taken here.
        var h = new Harness(Fetched(Inline("10", NewFinding)), carried: Holding(), row: Holding())
        {
            WritesLand = false,
        };

        await h.PollAsync();

        Assert.NotNull(h.Fired);
        Assert.Contains(NewFinding, h.Fired!.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_moving_the_signal_and_the_ledger_agree_as_they_always_did()
    {
        var incoming = Inline("10", NewFinding);
        var h = new Harness(Fetched(incoming), carried: Holding(), row: Holding());

        await h.PollAsync();

        Assert.NotNull(h.Fired);
        Assert.Equal(PrNodeEdges.OnComment, h.Fired!.EdgeName);
        Assert.Contains(NewFinding, h.Fired.Output, StringComparison.Ordinal);
        Assert.Contains(PrCommentLedger.KeyFor("review", "10"),
            PrCommentLedgerJson.TryParse(h.Row)!.DeliveredIds);
    }
}
