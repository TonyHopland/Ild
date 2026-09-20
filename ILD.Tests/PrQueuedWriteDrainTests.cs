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
/// Every write to a pull request goes through the PR node. An agent records
/// what it intends to say and nothing reaches the forge until the node runs, so
/// a person has the whole round to read it and drop any of it — and a chat
/// agent, which has no loop run driving it at all, cannot post either.
///
/// This drives the node's own path: what it sends, what it does not send after
/// a human has dropped it, and that its own answers never fire the comment edge
/// back at the loop.
/// </summary>
public class PrQueuedWriteDrainTests
{
    private const string CloneUrl = "https://example.com/owner/repo.git";
    private const string RepoUrl = "https://example.com/owner/repo";
    private const string PrUrl = "https://example.com/owner/repo/pull/42";
    private const string Head = "c19dc2d1237a7d283e38346ec952c60f762cca2e";

    private static PrQueuedWrite Reply(string id, string commentId, string body)
        => new(id, PrQueuedWrite.Reply, commentId, body, "src/A.cs", 10, DateTime.UtcNow);

    private static PrQueuedWrite Resolve(string id, string threadId)
        => new(id, PrQueuedWrite.Resolve, threadId, null, "src/A.cs", 10, DateTime.UtcNow);

    private sealed class Fixture
    {
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopRunStore> Runs { get; } = new();
        public LoopRun Run { get; }
        public List<(string Kind, string Target, string? Body)> Written { get; } = new();
        public string? RecordedLedger { get; private set; }
        public List<string?> QueueWrites { get; } = new();
        public string? PostedComment { get; private set; }

        public Fixture(IReadOnlyList<PrQueuedWrite> queued)
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                PrUrl = PrUrl,
                CurrentNodeId = Guid.NewGuid(),
                PrCommentQueue = queued.Count == 0 ? null : PrCommentQueueJson.Serialize(queued),
            };

            Remote.Setup(r => r.ReplyToReviewThreadAsync(CloneUrl, "42", It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string, string, string>((_, _, target, body) => Written.Add((PrQueuedWrite.Reply, target, body)))
                .ReturnsAsync(new RemotePrWriteResult(true, "4053396920", null));
            Remote.Setup(r => r.ResolveReviewThreadAsync(CloneUrl, "42", It.IsAny<string>()))
                .Callback<string, string, string>((_, _, target) => Written.Add((PrQueuedWrite.Resolve, target, null)))
                .ReturnsAsync(new RemotePrWriteResult(true, "t1", null));
            Remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "42", It.IsAny<string>()))
                .Callback<string, string, string>((_, _, body) => PostedComment = body)
                .ReturnsAsync(new RemotePrWriteResult(true, "5000000001", null));

            Runs.Setup(s => s.SetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => RecordedLedger = json)
                .Returns(Task.CompletedTask);
            Runs.Setup(s => s.SetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => { QueueWrites.Add(json); Run.PrCommentQueue = json; })
                .Returns(Task.CompletedTask);
        }

        public async Task<List<NodeOutcome>> RunNodeAsync(string? commentTemplate = null)
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
            services.AddSingleton(Runs.Object);

            var node = new LoopNode
            {
                Id = Run.CurrentNodeId!.Value,
                NodeType = NodeType.PR,
                Config = commentTemplate is null
                    ? "{}"
                    : "{\"prCommentTemplate\":" + JsonSerializer.Serialize(commentTemplate) + "}",
            };

            var outcomes = new List<NodeOutcome>();
            await foreach (var outcome in new PRNodeExecutor()
                .ExecuteAsync(new NodeExecutionContext(Run, node, services.BuildServiceProvider(), CancellationToken.None)))
                outcomes.Add(outcome);
            return outcomes;
        }
    }

    [Fact]
    public async Task The_pr_node_is_where_a_queued_reply_and_resolve_finally_go_out()
    {
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles."), Resolve("w2", "PRRT_t1") });

        await f.RunNodeAsync();

        Assert.Equal(2, f.Written.Count);
        Assert.Equal((PrQueuedWrite.Reply, "4049159495", f.Written[0].Body), f.Written[0]);
        Assert.Contains("That compiles.", f.Written[0].Body!, StringComparison.Ordinal);
        Assert.Equal(PrQueuedWrite.Resolve, f.Written[1].Kind);
        Assert.Equal("PRRT_t1", f.Written[1].Target);
    }

    [Fact]
    public async Task A_queued_reply_is_stamped_only_when_it_is_posted()
    {
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });

        await f.RunNodeAsync();

        var sent = Assert.Single(f.Written).Body!;
        Assert.True(PrCommentMarker.IsStamped(sent), $"the posted reply carries no marker:\n{sent}");
        Assert.True(
            sent.Contains(f.Run.Id.ToString("N"), StringComparison.OrdinalIgnoreCase),
            "the marker does not say which run wrote it");
    }

    [Fact]
    public async Task What_the_round_posted_never_fires_the_comment_edge_back_at_it()
    {
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });

        await f.RunNodeAsync();

        // The reply comes back on the next read as any other comment would.
        var ourReply = new RemotePrReviewLedger(
            Array.Empty<RemotePrReviewSummary>(),
            new[]
            {
                new RemotePrReviewItem("review", "4053396920", "PRRT_t1", null, "src/A.cs", 10,
                    f.Written[0].Body!, "ild-service-account", Head, DateTime.UtcNow, false, false),
            },
            Head, null);

        Assert.Empty(PrCommentDelivery.Decide(
            ourReply, Head, PrCommentLedgerJson.TryParse(f.RecordedLedger)).Items);
    }

    [Fact]
    public async Task A_reply_a_human_dropped_is_never_posted_and_leaves_its_finding_undelivered()
    {
        // The human removes the answer before the round reaches the node. The
        // thread stays open, nothing is recorded as written, and the finding
        // comes back on the next review rather than vanishing.
        var kept = Reply("w2", "4051372317", "And this one stands.");
        var f = new Fixture(new[] { kept });

        await f.RunNodeAsync();

        var written = Assert.Single(f.Written);
        Assert.Equal("4051372317", written.Target);
        Assert.DoesNotContain(f.Written, w => w.Target == "4049159495");

        // The dropped finding was never marked as something ILD answered, so a
        // later review carrying it fires.
        var stillOutstanding = new RemotePrReviewLedger(
            Array.Empty<RemotePrReviewSummary>(),
            new[]
            {
                new RemotePrReviewItem("review", "4049159495", "PRRT_t9", null, "src/A.cs", 10,
                    "the finding nobody answered", "Copilot", Head, DateTime.UtcNow, false, false),
            },
            Head, null);
        var watching = PrCommentLedgerJson.TryParse(f.RecordedLedger);
        Assert.Single(PrCommentDelivery.Decide(stillOutstanding, Head, watching).Items);
    }

    [Fact]
    public async Task The_queue_is_emptied_once_it_has_gone_out()
    {
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });

        await f.RunNodeAsync();

        Assert.Contains(null, f.QueueWrites);
        Assert.Null(f.Run.PrCommentQueue);
    }

    [Fact]
    public async Task A_round_that_only_queued_replies_still_drains_them_with_no_comment_template()
    {
        // A round may answer threads without the node having a comment to post;
        // those answers must still go out.
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });

        var outcomes = await f.RunNodeAsync(commentTemplate: null);

        Assert.Single(f.Written);
        Assert.Null(f.PostedComment);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task A_refused_write_does_not_fail_the_round_and_does_not_retry_for_ever()
    {
        // Nothing is lost by a refusal: the thread stays open and the finding
        // comes back. Failing here would park the run on something no human
        // asked for, and keeping the item would retry it every round.
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });
        f.Remote.Setup(r => r.ReplyToReviewThreadAsync(CloneUrl, "42", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrWriteResult(false, null, "403 from the forge"));

        var outcomes = await f.RunNodeAsync();

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.Null(f.Run.PrCommentQueue);
        Assert.Null(f.RecordedLedger);
    }

    [Fact]
    public async Task A_round_that_both_comments_and_answers_threads_does_both()
    {
        // The usual shape: the node has a pr_reply template AND the round
        // queued answers. Draining only on the template-less path would leave
        // every ordinary loop's answers stuck in the queue for ever.
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles."), Resolve("w2", "PRRT_t1") });

        var outcomes = await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.NotNull(f.PostedComment);
        Assert.Contains("Answered every point.", f.PostedComment!, StringComparison.Ordinal);
        Assert.Equal(2, f.Written.Count);
        Assert.Null(f.Run.PrCommentQueue);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task A_run_with_nothing_queued_writes_nothing_and_touches_no_queue()
    {
        var f = new Fixture(Array.Empty<PrQueuedWrite>());

        await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.Empty(f.Written);
        Assert.Empty(f.QueueWrites);
        Assert.NotNull(f.PostedComment);
    }
}
