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
/// The PR node posts a comment at the end of every round. Unmarked, that comment
/// is indistinguishable from a reviewer's and starts the next round — round,
/// comment, round, comment, each one a full verification gate with no human in
/// the way. This drives the node's own comment path and then feeds what it wrote
/// back through the heartbeat.
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

        public Fixture(RemotePrWriteResult result, bool withRunStore = true)
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                PrUrl = PrUrl,
                CurrentNodeId = Guid.NewGuid(),
            };
            Remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "42", It.IsAny<string>()))
                .Callback<string, string, string>((_, _, body) => PostedBody = body)
                .ReturnsAsync(result);
            Runs.Setup(s => s.SetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => RecordedLedger = json)
                .Returns(Task.CompletedTask);
            WithRunStore = withRunStore;
        }

        private bool WithRunStore { get; }

        public async Task<List<NodeOutcome>> RunNodeAsync(string template = "Answered every point in the review.")
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

            var node = new LoopNode
            {
                Id = Run.CurrentNodeId!.Value,
                NodeType = NodeType.PR,
                Config = "{\"prCommentTemplate\":" + JsonSerializer.Serialize(template) + "}",
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
    public async Task A_refused_comment_still_fails_the_node()
    {
        var f = new Fixture(new RemotePrWriteResult(false, null, "403 from the forge"));

        var outcomes = await f.RunNodeAsync();

        Assert.Contains(outcomes, o => o is NodeOutcome.Fail);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WaitingAction);
    }

    [Fact]
    public async Task The_node_still_posts_and_parks_where_no_run_store_is_available()
    {
        var f = new Fixture(new RemotePrWriteResult(true, "4053396920", null), withRunStore: false);

        var outcomes = await f.RunNodeAsync();

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.True(PrCommentMarker.IsStamped(f.PostedBody));
    }
}
