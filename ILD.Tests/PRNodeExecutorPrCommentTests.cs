using ILD.Data;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A round that has something general to say queues a comment, and the PR node
/// sends it. Unmarked, that comment is indistinguishable from a reviewer's and
/// starts the next round — round, comment, round, comment, each one a full
/// verification gate with no human in the way. This drives the path the comment
/// really goes out on and then feeds what it wrote back through the heartbeat.
/// </summary>
public class PRNodeExecutorPrCommentTests
{
    private const string CloneUrl = "https://example.com/owner/repo.git";
    private const string PrUrl = "https://example.com/owner/repo/pull/42";
    private const string Head = "c19dc2d1237a7d283e38346ec952c60f762cca2e";

    private sealed class Fixture : IDisposable
    {
        public TestDb Db { get; } = new();
        public PrNodeBuilder Node { get; } = new(cloneUrl: CloneUrl);
        public Mock<IRemoteProvider> Remote => Node.Remote;
        public LoopRun Run => Node.Run;
        public string? PostedBody { get; private set; }

        public string? RecordedLedger => ColumnOf(r => r.PrCommentLedger);

        /// <summary>The persisted queue column, which is what the node reads and claims.</summary>
        public string? Row => ColumnOf(r => r.PrCommentQueue);

        private readonly RemotePrWriteResult _result;
        private readonly bool _withRunStore;

        public static async Task<Fixture> CreateAsync(RemotePrWriteResult result, bool withRunStore = true)
        {
            var f = new Fixture(result, withRunStore);
            await f.InitAsync();
            return f;
        }

        private Fixture(RemotePrWriteResult result, bool withRunStore)
        {
            _result = result;
            _withRunStore = withRunStore;
        }

        private async Task InitAsync()
        {
            Run.PrUrl = PrUrl;
            Run.CurrentNodeId = Node.Node.Id;
            await PrRunSeed.SeedAsync(Db, prUrl: PrUrl, workItemId: "WI-1", currentNodeId: Node.Node.Id, id: Run.Id);
            // What the round queued: one general comment, tied to no item.
            await PrRunSeed.SetQueueAsync(Db, Run.Id, PrCommentQueueJson.Serialize(new[]
            {
                new PrQueuedWrite("w1", PrQueuedWrite.Comment, string.Empty,
                    "Answered every point in the review.", null, null, DateTime.UtcNow),
            }));
            Remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "42", It.IsAny<string>()))
                .Callback<string, string, string>((_, _, body) => PostedBody = body)
                .ReturnsAsync(_result);
            if (_withRunStore)
                Node.Runs = PrRunSeed.OverRealWrites(new LoopRunStore(Db.Fresh())).Object;
        }

        private string? ColumnOf(System.Linq.Expressions.Expression<Func<LoopRun, string?>> column)
            => Db.Fresh().LoopRuns.AsNoTracking().Where(r => r.Id == Run.Id).Select(column).Single();

        public Task<List<NodeOutcome>> RunNodeAsync() => Node.RunAsync();

        /// <summary>The heartbeat's view of the same PR one tick later, with ILD's own comment on it.</summary>
        public async Task<Mock<ILoopEngine>> PollAfterAsync(string commentId, string body)
        {
            var loopNodeId = Run.CurrentNodeId!.Value;
            await PrRunSeed.ParkAsync(Db, Run.Id);
            // The heartbeat runs in its own scope: it reads the row back, not the
            // instance the PR node was holding.
            var run = await PrRunSeed.ReadAsync(Db, Run.Id);
            var runNode = new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = run.Id,
                LoopNodeId = loopNodeId,
                Status = LoopRunNodeStatus.WaitingHuman,
            };

            var runs = PrRunSeed.OverRealWrites(new LoopRunStore(Db.Fresh()));
            runs.Setup(s => s.GetPrAwaitingMergeRunsAsync()).ReturnsAsync(new[] { run });
            runs.Setup(s => s.GetRunNodeAsync(run.Id, loopNodeId)).ReturnsAsync(runNode);
            runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new[]
            {
                new LoopNodeEdge
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = loopNodeId,
                    TargetNodeId = Guid.NewGuid(),
                    EdgeType = EdgeType.Custom,
                    Name = LoopOutputs.OnComment,
                },
            });

            var remote = new Mock<IRemoteProvider>();
            remote.Setup(r => r.GetPullRequestSnapshotAsync("https://example.com/owner/repo", "42"))
                .ReturnsAsync(new RemotePrSnapshot("t", "b", "open", false, null, null, RemotePrCiStatus.None,
                    Array.Empty<RemotePrCheck>(), false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow));
            remote.Setup(r => r.GetPullRequestReviewLedgerAsync("https://example.com/owner/repo", "42"))
                .ReturnsAsync(new RemotePrReviewLedger(
                    Array.Empty<RemotePrReviewSummary>(),
                    new[]
                    {
                        new RemotePrReviewItem("issue", commentId, null, null, null, null, body, "ild-service-account",
                            Head, DateTime.UtcNow, false, false),
                    },
                    Head, null));

            var engine = new Mock<ILoopEngine>();
            await new PrStatusPollService(runs.Object, remote.Object, engine.Object, Mock.Of<IRunNotifier>(),
                NullLogger<PrStatusPollService>.Instance).PollOnceAsync();
            return engine;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task The_comment_the_node_posts_carries_ilds_marker_and_the_run_it_came_from()
    {
        using var f = await Fixture.CreateAsync(new RemotePrWriteResult(true, "4053396920", null));

        await f.RunNodeAsync();

        Assert.NotNull(f.PostedBody);
        Assert.Contains("Answered every point in the review.", f.PostedBody!, StringComparison.Ordinal);
        Assert.True(PrCommentMarker.IsStamped(f.PostedBody), $"the posted comment carries no marker:\n{f.PostedBody}");
        Assert.True(
            f.PostedBody!.Contains(f.Run.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            || f.PostedBody!.Contains(f.Run.Id.ToString("N"), StringComparison.OrdinalIgnoreCase),
            "the marker does not say which run wrote the comment");
    }

    [Fact]
    public async Task A_round_that_ends_by_posting_its_answer_leaves_the_loop_idle()
    {
        using var f = await Fixture.CreateAsync(new RemotePrWriteResult(true, "4053396920", null));
        await f.RunNodeAsync();
        Assert.NotNull(f.RecordedLedger);

        var engine = await f.PollAfterAsync("4053396920", f.PostedBody!);

        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task The_recorded_id_alone_is_enough_when_the_marker_has_been_edited_away()
    {
        using var f = await Fixture.CreateAsync(new RemotePrWriteResult(true, "4053396920", null));
        await f.RunNodeAsync();
        Assert.NotNull(f.RecordedLedger);

        var engine = await f.PollAfterAsync("4053396920", "Answered every point in the review.");

        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task Someone_elses_comment_saying_exactly_the_same_thing_still_fires()
    {
        using var f = await Fixture.CreateAsync(new RemotePrWriteResult(true, "4053396920", null));
        await f.RunNodeAsync();

        var engine = await f.PollAfterAsync("4053396999", "Answered every point in the review.");

        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.Is<NodeSignal>(s => s.EdgeName == LoopOutputs.OnComment)), Times.Once);
    }

    [Fact]
    public async Task A_comment_the_provider_accepted_but_could_not_name_is_not_a_node_failure()
    {
        // Today's semantics: the node fails only on a refused post. An id the
        // response did not carry costs the ledger one entry, not the round.
        using var f = await Fixture.CreateAsync(new RemotePrWriteResult(true, null, null));

        var outcomes = await f.RunNodeAsync();

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.True(PrCommentMarker.IsStamped(f.PostedBody));
    }

    [Fact]
    public async Task A_refused_comment_does_not_fail_the_round_that_wrote_it()
    {
        // The comment is one of the round's queued writes now, and a refused
        // write never fails the node: failing would park the run on something no
        // human asked for, and the refusal is already recorded and logged.
        using var f = await Fixture.CreateAsync(new RemotePrWriteResult(false, null, "403 from the forge"));

        var outcomes = await f.RunNodeAsync();

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        // …and it is not left queued to be retried for ever against a forge that
        // keeps saying no.
        Assert.Null(f.Row);
    }

    [Fact]
    public async Task With_no_run_store_there_is_no_queue_to_send_and_the_node_still_parks()
    {
        // The intents live nowhere but that row, so without it there is nothing
        // to post — and the node must park rather than fail for want of a store.
        using var f = await Fixture.CreateAsync(new RemotePrWriteResult(true, "4053396920", null), withRunStore: false);

        var outcomes = await f.RunNodeAsync();

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.Null(f.PostedBody);
    }
}
