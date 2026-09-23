using System.Text.Json;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A run watches its own pull request from the moment that pull request
/// existed.
///
/// The ledger used to stay null until the node posted something, so the first
/// heartbeat found no ledger and recorded EVERYTHING already on the pull
/// request as history it had seen. Anything said between opening the pull
/// request and that first poll was therefore never handed over — normally up to
/// one heartbeat interval, unbounded if polls fail. Seen for real on Forgejo:
/// a comment posted 28 s after the pull request opened never appeared in an
/// edge firing.
/// </summary>
public class PrWatchedFromCreationTests
{
    private const string CloneUrl = "https://example.com/owner/repo.git";
    private const string PrUrl = "https://example.com/owner/repo/pull/42";
    private const string Head = "c19dc2d1237a7d283e38346ec952c60f762cca2e";

    private static RemotePrReviewItem Issue(string id, string body, DateTime createdAt)
        => new("issue", id, null, null, null, null, body, "tony", Head, createdAt, false, false);

    /// <summary>Drives the PR node's CREATE path and hands back the ledger it opened.</summary>
    private static async Task<string?> CreatePrAsync()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            CurrentNodeId = Guid.NewGuid(),
            // No pull request yet: this is the round that opens one.
            PrUrl = null,
        };

        string? recorded = null;
        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetPrCommentLedgerAsync(It.IsAny<Guid>())).ReturnsAsync(() => run.PrCommentLedger);
        runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) =>
            {
                if (!string.Equals(expected, run.PrCommentLedger, StringComparison.Ordinal)) return false;
                run.PrCommentLedger = json;
                recorded = json;
                return true;
            });

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestAsync(CloneUrl, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(PrUrl, PrUrl, RemotePrStatus.Open, null));

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
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        services.AddSingleton(runs.Object);

        var node = new LoopNode
        {
            Id = run.CurrentNodeId!.Value,
            NodeType = NodeType.PR,
            Config = "{}",
        };

        await foreach (var _ in new PRNodeExecutor()
            .ExecuteAsync(new NodeExecutionContext(run, node, services.BuildServiceProvider(), CancellationToken.None)))
        {
        }

        return recorded;
    }

    [Fact]
    public async Task Opening_a_pull_request_opens_the_ledger_with_it()
    {
        var before = DateTime.UtcNow;

        var ledger = PrCommentLedgerJson.TryParse(await CreatePrAsync());

        Assert.NotNull(ledger);
        Assert.NotNull(ledger!.WatchedFrom);
        // Stamped before the create call, so nothing posted while the pull
        // request was being opened can fall outside the window — and back-dated
        // by the skew allowance on top, because this stamp is ILD's clock and
        // what it is compared against is the forge's. Nothing can be
        // over-delivered into the widened span: no comment predates the pull
        // request it is on.
        Assert.InRange(
            ledger.WatchedFrom!.Value,
            before - PrCommentLedger.ClockSkewAllowance - TimeSpan.FromSeconds(5),
            DateTime.UtcNow - PrCommentLedger.ClockSkewAllowance);
    }

    [Fact]
    public async Task A_comment_posted_before_the_first_poll_still_fires_on_it()
    {
        var ledger = PrCommentLedgerJson.TryParse(await CreatePrAsync());
        Assert.NotNull(ledger);

        // 28 s after the pull request opened, and before the first heartbeat.
        var posted = Issue("125", "I dont like this, remove it", ledger!.WatchedFrom!.Value.AddSeconds(28));
        var fetched = new RemotePrReviewLedger(
            Array.Empty<RemotePrReviewSummary>(), new[] { posted }, Head, null);

        var delivered = PrCommentDelivery.Decide(fetched, Head, ledger);

        var item = Assert.Single(delivered.Items);
        Assert.Equal("125", item.CommentId);
    }

    [Fact]
    public async Task What_predates_the_pull_request_is_still_history()
    {
        var ledger = PrCommentLedgerJson.TryParse(await CreatePrAsync());
        Assert.NotNull(ledger);

        var older = Issue("1", "from another life", ledger!.WatchedFrom!.Value.AddMinutes(-5));
        var fetched = new RemotePrReviewLedger(
            Array.Empty<RemotePrReviewSummary>(), new[] { older }, Head, null);

        Assert.Empty(PrCommentDelivery.Decide(fetched, Head, ledger).Items);
    }

    [Fact]
    public void A_run_that_predates_this_still_seeds_from_what_is_there()
    {
        // It did not open that pull request and has no claim to have been
        // watching it, so the first poll records what is there and fires
        // nothing — the behaviour before this existed.
        var fetched = new RemotePrReviewLedger(
            Array.Empty<RemotePrReviewSummary>(),
            new[] { Issue("1", "said before this run ever looked", DateTime.UtcNow.AddMinutes(-5)) },
            Head, null);

        Assert.Empty(PrCommentDelivery.Decide(fetched, Head, state: null).Items);
    }
}
