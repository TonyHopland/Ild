using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The PR node's first visit — the one where the run has no pull request yet —
/// looks for an open pull request on the run's branch and then attaches to it,
/// creates one, or fails, as its two toggles allow. A run that attaches did not
/// open that pull request: its history is not news, and an <c>on_existing_pr</c>
/// edge carries the run straight on to whatever reviews it.
/// </summary>
public class PRNodeExecutorAttachTests
{
    private const string CloneUrl = "https://example.com/o/r.git";
    private const string Branch = "feature/x";
    private const string ExistingPrUrl = "https://example.com/o/r/pull/17";
    private const string CreatedPrUrl = "https://example.com/o/r/pull/42";
    private const string Head = "c19dc2d1237a7d283e38346ec952c60f762cca2e";

    private static readonly RemotePrLookup NoOpenPr = new(null, null, null, null);
    private static readonly RemotePrLookup OpenPr = new(17, ExistingPrUrl, ExistingPrUrl, null);

    private sealed class Harness
    {
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRepositoryManager> Repo { get; } = new();
        public LoopRun Run { get; }
        public LoopNode Node { get; }
        public string? QueueRow { get; set; }
        public List<LoopNodeEdge> Edges { get; } = new();
        public string[] Tags { get; set; } = Array.Empty<string>();

        public Harness(string config = "{}", string? prUrl = null)
        {
            Node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = config };
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                BranchName = Branch,
                PrUrl = prUrl,
                CurrentNodeId = Node.Id,
            };

            Runs.Setup(s => s.GetPrCommentQueueAsync(It.IsAny<Guid>())).ReturnsAsync(() => QueueRow);
            Runs.Setup(s => s.TrySetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, QueueRow, StringComparison.Ordinal)) return false;
                    QueueRow = json;
                    return true;
                });
            Runs.Setup(s => s.GetPrCommentLedgerAsync(It.IsAny<Guid>())).ReturnsAsync(() => Run.PrCommentLedger);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentLedger, StringComparison.Ordinal)) return false;
                    Run.PrCommentLedger = json;
                    return true;
                });
            Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync(() => Edges.ToArray());

            Remote.Setup(r => r.CreatePullRequestAsync(CloneUrl, Branch, "main", It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new RemotePrResult(CreatedPrUrl, CreatedPrUrl, RemotePrStatus.Open, null));
            Remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new RemotePrWriteResult(true, "900", null));
        }

        public void Finds(RemotePrLookup lookup)
            => Remote.Setup(r => r.FindOpenPullRequestAsync(CloneUrl, Branch, "main", It.IsAny<CancellationToken>())).ReturnsAsync(lookup);

        public void WireOnExistingPr()
            => Edges.Add(new LoopNodeEdge
            {
                Id = Guid.NewGuid(),
                SourceNodeId = Node.Id,
                TargetNodeId = Guid.NewGuid(),
                EdgeType = EdgeType.Custom,
                Name = LoopOutputs.OnExistingPr,
            });

        public void QueueComment(string body)
            => QueueRow = PrCommentQueueJson.Serialize(new[]
            {
                new PrQueuedWrite("w1", PrQueuedWrite.Comment, string.Empty, body, null, null, DateTime.UtcNow),
            });

        public async Task<List<NodeOutcome>> RunNodeAsync(string? worktree = null)
        {
            var repoId = Guid.NewGuid();
            Run.WorktreePath = worktree;
            var workItems = new Mock<IWorkItemManager>();
            workItems.Setup(m => m.GetWorkItemAsync("WI-1")).ReturnsAsync(new WorkItemView
            {
                Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId, Tags = Tags,
            });
            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(new Repository
            {
                Id = repoId, Name = "r", CloneUrl = CloneUrl, DefaultBranch = "main", RemoteProviderId = Guid.NewGuid(),
            });
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

            var services = new ServiceCollection();
            services.AddSingleton(workItems.Object);
            services.AddSingleton(providerStore.Object);
            services.AddSingleton(Remote.Object);
            services.AddSingleton(Repo.Object);
            services.AddSingleton(Runs.Object);

            var outcomes = new List<NodeOutcome>();
            await foreach (var o in new PRNodeExecutor().ExecuteAsync(
                new NodeExecutionContext(Run, Node, services.BuildServiceProvider(), CancellationToken.None)))
                outcomes.Add(o);
            return outcomes;
        }

        public void VerifyNeverCreated()
            => Remote.Verify(r => r.CreatePullRequestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static bool IsOnExistingPr(NodeOutcome o)
        => o is NodeOutcome.Success { Edge: EdgeType.Custom, EdgeName: LoopOutputs.OnExistingPr };

    // -- The toggle table ------------------------------------------------------

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"attachIfExists":true,"createIfNoneExists":false}""")]
    public async Task An_open_pr_on_the_branch_is_attached_to_rather_than_created(string config)
    {
        var h = new Harness(config) { Tags = new[] { "AutoMerge" } };
        h.Finds(OpenPr);

        var outcomes = await h.RunNodeAsync();

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Equal(ExistingPrUrl, outcomes.OfType<NodeOutcome.PrAttached>().Single().PrUrl);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrCreated);
        h.VerifyNeverCreated();
        // Not this run's pull request to have merged on its own.
        h.Remote.Verify(r => r.EnablePullRequestAutoMergeAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"attachIfExists":false,"createIfNoneExists":true}""")]
    public async Task With_no_open_pr_on_the_branch_the_node_creates_one_and_parks(string config)
    {
        var h = new Harness(config);
        h.Finds(NoOpenPr);

        var outcomes = await h.RunNodeAsync();

        Assert.Equal(CreatedPrUrl, outcomes.OfType<NodeOutcome.PrCreated>().Single().PrUrl);
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, outcomes.OfType<NodeOutcome.WaitingAction>().Single().Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrAttached);
    }

    [Fact]
    public async Task An_attach_only_node_with_no_open_pr_fails_instead_of_creating()
    {
        var h = new Harness("""{"attachIfExists":true,"createIfNoneExists":false}""");
        h.Finds(NoOpenPr);

        var outcomes = await h.RunNodeAsync();

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("No open PR for branch `feature/x`; this node does not create PRs", fail.Reason);
        h.VerifyNeverCreated();
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrAttached or NodeOutcome.PrCreated or NodeOutcome.WaitingAction);
    }

    [Fact]
    public async Task A_create_only_node_finding_an_open_pr_fails_instead_of_attaching()
    {
        var h = new Harness("""{"attachIfExists":false,"createIfNoneExists":true}""");
        h.WireOnExistingPr();
        h.Finds(OpenPr);

        var outcomes = await h.RunNodeAsync();

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("PR #17 already exists for branch `feature/x`; this node does not attach to existing PRs", fail.Reason);
        h.VerifyNeverCreated();
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrAttached or NodeOutcome.PrCreated);
        Assert.DoesNotContain(outcomes, IsOnExistingPr);
    }

    [Fact]
    public async Task A_lookup_the_forge_could_not_answer_fails_the_node_and_creates_nothing()
    {
        var h = new Harness();
        h.Finds(new RemotePrLookup(null, null, null, "HTTP 502 from the forge"));

        var outcomes = await h.RunNodeAsync();

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("HTTP 502 from the forge", fail.Reason);
        h.VerifyNeverCreated();
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrAttached or NodeOutcome.PrCreated);
    }

    // -- Attaching -------------------------------------------------------------

    [Fact]
    public async Task Attaching_skips_the_no_commits_ahead_guard()
    {
        // A review run has nothing of its own on the branch yet: it is someone
        // else's work, and the pull request that guard protects against already exists.
        var worktree = Path.Combine(Path.GetTempPath(), "ild-pr-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var h = new Harness();
            h.Finds(OpenPr);
            h.Repo.Setup(m => m.GetDiffAsync(worktree)).ReturnsAsync(string.Empty);
            h.Repo.Setup(m => m.PushAsync(worktree, Branch, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync((true, (string?)null));
            h.Repo.Setup(m => m.GetCommitsAheadCountAsync(worktree, It.IsAny<string>())).ReturnsAsync(0);

            var outcomes = await h.RunNodeAsync(worktree);

            Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
            Assert.Equal(ExistingPrUrl, outcomes.OfType<NodeOutcome.PrAttached>().Single().PrUrl);
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Attaching_watches_the_pr_from_the_moment_of_attaching_so_its_history_is_not_news()
    {
        var h = new Harness();
        h.Finds(OpenPr);
        var before = DateTime.UtcNow;

        await h.RunNodeAsync();

        var ledger = PrCommentLedgerJson.TryParse(h.Run.PrCommentLedger);
        Assert.NotNull(ledger);
        Assert.NotNull(ledger!.WatchedFrom);
        Assert.InRange(ledger.WatchedFrom!.Value, before, DateTime.UtcNow);

        // Said on the pull request a moment before this run attached: history.
        var older = new RemotePrReviewItem("issue", "1", null, null, null, null, "an old remark", "alice", Head,
            before.AddSeconds(-1), false, false);
        Assert.Empty(PrCommentDelivery.Decide(
            new RemotePrReviewLedger(Array.Empty<RemotePrReviewSummary>(), new[] { older }, Head, null), Head, ledger).Items);

        // Said after: the run is watching, so it is handed over.
        var newer = new RemotePrReviewItem("issue", "2", null, null, null, null, "a new remark", "alice", Head,
            DateTime.UtcNow.AddSeconds(1), false, false);
        Assert.Equal("2", Assert.Single(PrCommentDelivery.Decide(
            new RemotePrReviewLedger(Array.Empty<RemotePrReviewSummary>(), new[] { newer }, Head, null), Head, ledger).Items).CommentId);
    }

    // -- on_existing_pr --------------------------------------------------------

    [Fact]
    public async Task Attaching_with_on_existing_pr_wired_sends_the_queue_and_routes_on_without_parking()
    {
        var h = new Harness();
        h.WireOnExistingPr();
        h.Finds(OpenPr);
        h.QueueComment("Picking this review up.");

        var outcomes = await h.RunNodeAsync();

        var routed = outcomes.Single(IsOnExistingPr);
        var output = ((NodeOutcome.Success)routed).Output;
        Assert.NotNull(output);
        Assert.Contains(ExistingPrUrl, output!);
        Assert.Contains("#17", output);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WaitingAction or NodeOutcome.Fail);
        h.Remote.Verify(r => r.CreatePullRequestCommentAsync(CloneUrl, "17",
            It.Is<string>(b => b.Contains("Picking this review up."))), Times.Once);
        Assert.Null(h.QueueRow);
    }

    [Fact]
    public async Task Attaching_with_on_existing_pr_unwired_parks_like_a_new_pr()
    {
        var h = new Harness();
        h.Finds(OpenPr);

        var outcomes = await h.RunNodeAsync();

        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, outcomes.OfType<NodeOutcome.WaitingAction>().Single().Reason);
        Assert.DoesNotContain(outcomes, IsOnExistingPr);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task Creating_a_pr_never_fires_on_existing_pr_even_when_it_is_wired()
    {
        var h = new Harness();
        h.WireOnExistingPr();
        h.Finds(NoOpenPr);

        var outcomes = await h.RunNodeAsync();

        Assert.Contains(outcomes, o => o is NodeOutcome.PrCreated);
        Assert.DoesNotContain(outcomes, IsOnExistingPr);
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, outcomes.OfType<NodeOutcome.WaitingAction>().Single().Reason);
    }

    [Fact]
    public async Task A_later_visit_ignores_the_toggles_sends_the_queue_and_parks()
    {
        // Attach-only, and on_existing_pr wired: on a first visit with no open PR
        // this node would fail, and on an attach it would route on. Neither
        // applies once the run has its pull request.
        var h = new Harness("""{"attachIfExists":true,"createIfNoneExists":false}""", prUrl: ExistingPrUrl);
        h.WireOnExistingPr();
        h.Finds(NoOpenPr);
        h.QueueComment("Second round of findings sent.");

        var outcomes = await h.RunNodeAsync();

        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, outcomes.OfType<NodeOutcome.WaitingAction>().Single().Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail or NodeOutcome.PrAttached or NodeOutcome.PrCreated);
        Assert.DoesNotContain(outcomes, IsOnExistingPr);
        h.Remote.Verify(r => r.FindOpenPullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.VerifyNeverCreated();
        h.Remote.Verify(r => r.CreatePullRequestCommentAsync(CloneUrl, "17",
            It.Is<string>(b => b.Contains("Second round of findings sent."))), Times.Once);
    }
}
