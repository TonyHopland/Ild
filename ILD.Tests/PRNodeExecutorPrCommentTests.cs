using System.Text.Json;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A round that has something general to say queues a comment, and the PR node
/// sends it. Unmarked, that comment is indistinguishable from a reviewer's and
/// starts the next round — round, comment, round, comment, each one a full
/// verification gate with no human in the way. This drives the path the comment
/// really goes out on and then feeds what it wrote back through the heartbeat.
///
/// The node no longer writes a comment of its own from `prCommentTemplate`: it
/// could only guess whether a round had anything left to say, and the guess was
/// wrong in one direction or the other every time. So the round says it, through
/// `comment_on_pr` — and everything that made the node's comment safe has to
/// hold for the round's, which is what these pin.
/// </summary>
public class PRNodeExecutorPrCommentTests
{
    private const string CloneUrl = "https://example.com/owner/repo.git";
    private const string PrUrl = "https://example.com/owner/repo/pull/42";
    private const string Head = "c19dc2d1237a7d283e38346ec952c60f762cca2e";

    private sealed class Fixture
    {
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopRunStore> Runs { get; } = new();
        public LoopRun Run { get; }
        public string? PostedBody { get; private set; }
        public string? RecordedLedger { get; private set; }

        /// <summary>The persisted queue column, which only the store doubles touch.</summary>
        public string? Row { get; set; }

        public Fixture(RemotePrWriteResult result, bool withRunStore = true)
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                PrUrl = PrUrl,
                CurrentNodeId = Guid.NewGuid(),
            };
            // What the round queued: one general comment, tied to no item.
            Row = PrCommentQueueJson.Serialize(new[]
            {
                new PrQueuedWrite("w1", PrQueuedWrite.Comment, string.Empty,
                    "Answered every point in the review.", null, null, DateTime.UtcNow),
            });
            Runs.Setup(s => s.GetPrCommentQueueAsync(It.IsAny<Guid>())).ReturnsAsync(() => Row);
            Runs.Setup(s => s.TrySetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Row, StringComparison.Ordinal)) return false;
                    Row = json;
                    return true;
                });
            Remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "42", It.IsAny<string>()))
                .Callback<string, string, string>((_, _, body) => PostedBody = body)
                .ReturnsAsync(result);
            // The ledger is mutated by compare-and-set: every writer reads the
            // column and writes only if it still holds what it read.
            Runs.Setup(s => s.GetPrCommentLedgerAsync(It.IsAny<Guid>())).ReturnsAsync(() => Run.PrCommentLedger);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentLedger, StringComparison.Ordinal)) return false;
                    Run.PrCommentLedger = json;
                    RecordedLedger = json;
                    return true;
                });
            WithRunStore = withRunStore;
        }

        private bool WithRunStore { get; }

        public async Task<List<NodeOutcome>> RunNodeAsync()
        {
            var repoId = Guid.NewGuid();
            var workItems = new Mock<IWorkItemManager>();
            workItems.Setup(m => m.GetWorkItemAsync("WI-1"))
                .ReturnsAsync(new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId });
            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(new Repository
            {
                Id = repoId, Name = "repo", CloneUrl = CloneUrl, DefaultBranch = "main", RemoteProviderId = Guid.NewGuid(),
            });
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

            var services = new ServiceCollection();
            services.AddSingleton(workItems.Object);
            services.AddSingleton(providerStore.Object);
            services.AddSingleton(Remote.Object);
            services.AddSingleton(Mock.Of<IRepositoryManager>());
            if (WithRunStore) services.AddSingleton(Runs.Object);

            // Deliberately no prCommentTemplate: the node ignores it now, and a
            // test that still set one could not tell an ignored field from a
            // working one.
            var node = new LoopNode
            {
                Id = Run.CurrentNodeId!.Value,
                NodeType = NodeType.PR,
                Config = "{}",
            };

            var outcomes = new List<NodeOutcome>();
            await foreach (var outcome in new PRNodeExecutor()
                .ExecuteAsync(new NodeExecutionContext(Run, node, services.BuildServiceProvider(), CancellationToken.None)))
                outcomes.Add(outcome);
            return outcomes;
        }
    }

    /// <summary>The heartbeat's view of the same PR one tick later, with ILD's own comment on it.</summary>
    private static async Task<Mock<ILoopEngine>> PollAfterAsync(LoopRun run, string? ledgerJson, string commentId, string body)
    {
        var loopNodeId = run.CurrentNodeId!.Value;
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = loopNodeId,
            Status = LoopRunNodeStatus.WaitingHuman,
        };
        run.Status = LoopRunStatus.WaitingHuman;
        run.HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge;
        // The heartbeat runs in its own scope: it reads the row back, not the
        // instance the PR node was holding.
        run.PrCommentLedger = ledgerJson;

        var runs = new Mock<ILoopRunStore>();
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
                Name = PrNodeEdges.OnComment,
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

    [Fact]
    public async Task The_comment_the_node_posts_carries_ilds_marker_and_the_run_it_came_from()
    {
        var f = new Fixture(new RemotePrWriteResult(true, "4053396920", null));

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
        var f = new Fixture(new RemotePrWriteResult(true, "4053396920", null));
        await f.RunNodeAsync();
        Assert.NotNull(f.RecordedLedger);

        var engine = await PollAfterAsync(f.Run, f.RecordedLedger, "4053396920", f.PostedBody!);

        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task The_recorded_id_alone_is_enough_when_the_marker_has_been_edited_away()
    {
        var f = new Fixture(new RemotePrWriteResult(true, "4053396920", null));
        await f.RunNodeAsync();
        Assert.NotNull(f.RecordedLedger);

        var engine = await PollAfterAsync(f.Run, f.RecordedLedger, "4053396920", "Answered every point in the review.");

        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task Someone_elses_comment_saying_exactly_the_same_thing_still_fires()
    {
        var f = new Fixture(new RemotePrWriteResult(true, "4053396920", null));
        await f.RunNodeAsync();

        var engine = await PollAfterAsync(f.Run, f.RecordedLedger, "4053396999", "Answered every point in the review.");

        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.Is<NodeSignal>(s => s.EdgeName == PrNodeEdges.OnComment)), Times.Once);
    }

    [Fact]
    public async Task A_comment_the_provider_accepted_but_could_not_name_is_not_a_node_failure()
    {
        // Today's semantics: the node fails only on a refused post. An id the
        // response did not carry costs the ledger one entry, not the round.
        var f = new Fixture(new RemotePrWriteResult(true, null, null));

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
        var f = new Fixture(new RemotePrWriteResult(false, null, "403 from the forge"));

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
        var f = new Fixture(new RemotePrWriteResult(true, "4053396920", null), withRunStore: false);

        var outcomes = await f.RunNodeAsync();

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.Null(f.PostedBody);
    }

    [Fact]
    public async Task A_round_with_nothing_queued_says_nothing_at_all()
    {
        // The change this whole design turns on: a round that answered on the
        // threads, or changed code and had nothing to add, leaves the pull
        // request quiet. There is no template comment behind it any more.
        var f = new Fixture(new RemotePrWriteResult(true, "4053396920", null)) { Row = null };

        var outcomes = await f.RunNodeAsync();

        Assert.Null(f.PostedBody);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
    }
}
