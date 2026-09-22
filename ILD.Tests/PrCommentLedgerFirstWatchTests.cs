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
/// A run can end up holding a ledger it never watched with: the PR node records
/// the comment it posts whether or not <c>on_comment</c> is wired, and that
/// write opens a ledger out of nothing. Left saying "watching, nothing seen",
/// the first tick after the edge is wired would hand the agent every comment the
/// pull request has ever carried — ILD's own pre-marker replies included, which
/// is the money pump this feature exists to prevent. Such a ledger therefore
/// carries the moment it was opened, and whoever watches first treats what
/// predates it as history.
/// </summary>
public class PrCommentLedgerFirstWatchTests
{
    private const string CloneUrl = "https://example.com/owner/repo.git";
    private const string PrUrl = "https://example.com/owner/repo/pull/42";
    private const string Head = "c19dc2d1237a7d283e38346ec952c60f762cca2e";

    private static RemotePrReviewItem Issue(string id, string body, DateTime createdAt)
        => new("issue", id, null, null, null, null, body, "tony", Head, createdAt, false, false);

    private static RemotePrReviewLedger Fetched(params RemotePrReviewItem[] items)
        => new(Array.Empty<RemotePrReviewSummary>(), items, Head, null);

    /// <summary>Drives the PR node's comment path and hands back the ledger it recorded.</summary>
    private static async Task<(string? Ledger, LoopRun Run)> PostCommentAsync(string? existingLedger)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            PrUrl = PrUrl,
            CurrentNodeId = Guid.NewGuid(),
            PrCommentLedger = existingLedger,
        };

        string? recorded = null;
        var runs = new Mock<ILoopRunStore>();
        // Compare-and-set, as every writer of this column now is.
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
        remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "42", It.IsAny<string>()))
            .ReturnsAsync(new RemotePrWriteResult(true, "4053396920", null));

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
            Config = "{\"prCommentTemplate\":" + JsonSerializer.Serialize("Answered every point.") + "}",
        };

        await foreach (var _ in new PRNodeExecutor()
            .ExecuteAsync(new NodeExecutionContext(run, node, services.BuildServiceProvider(), CancellationToken.None)))
        {
        }

        return (recorded, run);
    }

    [Fact]
    public async Task A_ledger_the_pr_node_opened_says_it_has_never_watched_the_pull_request()
    {
        var before = DateTime.UtcNow;

        var (recorded, _) = await PostCommentAsync(existingLedger: null);

        var ledger = PrCommentLedgerJson.TryParse(recorded);
        Assert.NotNull(ledger);
        Assert.NotNull(ledger!.WatchedFrom);
        Assert.InRange(ledger.WatchedFrom!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Merging_into_a_ledger_that_has_watched_does_not_reopen_it()
    {
        // The usual case: the heartbeat seeded while the run was parked, the
        // round ran, and the node is only adding the id of what it just posted.
        var watching = PrCommentLedgerJson.Serialize(
            PrCommentDelivery.Decide(Fetched(Issue("1", "ping", DateTime.UtcNow.AddHours(-1))), Head, null).Ledger);

        var (recorded, _) = await PostCommentAsync(watching);

        var ledger = PrCommentLedgerJson.TryParse(recorded);
        Assert.NotNull(ledger);
        Assert.Null(ledger!.WatchedFrom);
        Assert.Contains("issue:4053396920", ledger.PostedIds);
    }

    [Fact]
    public async Task Wiring_the_edge_after_a_round_has_posted_does_not_deliver_the_pull_requests_whole_past()
    {
        var (recorded, _) = await PostCommentAsync(existingLedger: null);
        var opened = PrCommentLedgerJson.TryParse(recorded);
        Assert.NotNull(opened);

        var history = Fetched(
            Issue("1", "an objection from three rounds ago", DateTime.UtcNow.AddHours(-3)),
            Issue("2", "and ILD's own answer, from before the marker existed", DateTime.UtcNow.AddHours(-2)));

        var decision = PrCommentDelivery.Decide(history, Head, opened);

        Assert.Empty(decision.Items);
        // …and the history is recorded, so it cannot fire once the stamp is gone.
        Assert.Null(decision.Ledger.WatchedFrom);
        Assert.Empty(PrCommentDelivery.Decide(history, Head, decision.Ledger).Items);
    }

    [Fact]
    public async Task What_arrives_after_the_node_posted_still_fires()
    {
        var (recorded, _) = await PostCommentAsync(existingLedger: null);
        var opened = PrCommentLedgerJson.TryParse(recorded);

        var decision = PrCommentDelivery.Decide(
            Fetched(
                Issue("1", "an objection from three rounds ago", DateTime.UtcNow.AddHours(-3)),
                Issue("99", "and one a person typed just now", DateTime.UtcNow.AddMinutes(5))),
            Head, opened);

        var item = Assert.Single(decision.Items);
        Assert.Equal("99", item.CommentId);
    }

    [Fact]
    public async Task The_comment_the_node_itself_posted_never_fires_whichever_side_of_the_stamp_it_lands()
    {
        var (recorded, run) = await PostCommentAsync(existingLedger: null);
        var opened = PrCommentLedgerJson.TryParse(recorded);
        var ours = PrCommentMarker.Stamp("Answered every point.", run.Id);

        foreach (var createdAt in new[] { DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(5) })
            Assert.Empty(PrCommentDelivery.Decide(
                Fetched(Issue("4053396920", ours, createdAt)), Head, opened).Items);
    }

    [Fact]
    public void A_ledger_written_before_this_field_existed_reads_as_one_that_has_watched()
    {
        var old = "{\"v\":1,\"head\":\"" + Head + "\",\"postedIds\":[],\"deliveredIds\":[\"issue:1\"],\"deliveredHashes\":[]}";

        var ledger = PrCommentLedgerJson.TryParse(old);

        Assert.NotNull(ledger);
        Assert.Null(ledger!.WatchedFrom);
    }

    [Fact]
    public void The_stamp_survives_a_round_trip()
    {
        var stamped = PrCommentLedger.Empty with { WatchedFrom = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc) };

        var back = PrCommentLedgerJson.TryParse(PrCommentLedgerJson.Serialize(stamped));

        Assert.Equal(stamped.WatchedFrom, back!.WatchedFrom!.Value.ToUniversalTime());
    }
}
