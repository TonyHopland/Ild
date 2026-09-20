using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// What a read leaves behind, and what a failed write does not. A read by the
/// round that is going to act on the items consumes them, so they do not also
/// start a round of their own — but only the items it actually returned, and
/// only when the write it recorded really happened.
/// </summary>
public class PrReviewServiceConsumptionTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string HeadA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HeadB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static RemotePrReviewItem Inline(string id, string body = "this allocation is wrong", string commit = HeadB)
        => new("review", id, $"PRRT_{id}", "r1", "src/A.cs", 10, body, "Copilot", commit,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Ledger(params RemotePrReviewItem[] items)
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", "body", HeadB, DateTime.UtcNow, "Copilot", false) },
            items, HeadB, null);

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public string? RecordedLedger { get; private set; }
        public string? RecordedQueue { get; private set; }
        public int LedgerWrites { get; private set; }

        public Harness(RemotePrReviewLedger ledger)
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-1",
                PrUrl = PrUrl,
                Status = LoopRunStatus.Running,
            };
            Runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(Run);
            Runs.Setup(s => s.GetByIdAsync(Run.Id)).ReturnsAsync(() => Run);
            Runs.Setup(s => s.SetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => { RecordedQueue = json; Run.PrCommentQueue = json; })
                .Returns(Task.CompletedTask);
            Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);
            Runs.Setup(s => s.SetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => { RecordedLedger = json; LedgerWrites++; })
                .Returns(Task.CompletedTask);
            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(ledger);
        }

        public PrReviewService Build() => new(Runs.Object, Remote.Object);
    }

    [Fact]
    public async Task A_read_narrowed_to_one_commit_consumes_only_what_it_returned()
    {
        // Asking "what is new since HeadA" must not quietly mark the HeadA
        // findings answered — the round never saw them.
        var fetched = Ledger(
            Inline("11", body: "an older finding", commit: HeadA),
            Inline("21", body: "and a newer one"));
        var h = new Harness(fetched);

        await h.Build().ReadAsync("wi-1", sinceCommit: HeadA, callerRunId: h.Run.Id);

        var outstanding = PrCommentDelivery.Decide(fetched, HeadB, PrCommentLedgerJson.TryParse(h.RecordedLedger));
        var item = Assert.Single(outstanding.Items);
        Assert.Equal("11", item.CommentId);
    }

    [Fact]
    public async Task Queuing_a_reply_reaches_the_forge_for_the_read_only_and_never_to_write()
    {
        // The ledger is still fetched — that is how the comment id is validated
        // while the agent is listening — but nothing is written.
        var h = new Harness(Ledger(Inline("11")));

        var result = await h.Build().ReplyAsync("wi-1", "11", "Answered.", h.Run.Id);

        Assert.True(result.Ok);
        h.Remote.Verify(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7"), Times.Once);
        h.Remote.Verify(
            r => r.ReplyToReviewThreadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        Assert.Equal(0, h.LedgerWrites);
    }

    [Fact]
    public async Task A_queue_that_has_grown_absurd_refuses_rather_than_growing_further()
    {
        // A looping agent must not be able to grow a run's column without bound,
        // and the refusal has to be readable rather than a silent drop.
        var h = new Harness(Ledger(Inline("11")));
        var service = h.Build();
        for (var i = 0; i < PrQueuedWrite.MaxQueued; i++)
            Assert.True((await service.ReplyAsync("wi-1", "11", $"answer {i}", h.Run.Id)).Ok);

        var refused = await service.ReplyAsync("wi-1", "11", "one too many", h.Run.Id);

        Assert.False(refused.Ok);
        Assert.Contains("waiting for the PR node", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PrQueuedWrite.MaxQueued, PrCommentQueueJson.TryParse(h.RecordedQueue).Count);
    }

    [Fact]
    public async Task A_forge_that_cannot_be_read_says_so_rather_than_that_the_id_does_not_exist()
    {
        // The id check is the scope gate; with no ledger to check against, every
        // id looks absent. Reporting an unreachable forge as "no such comment"
        // sends the agent off inventing ids instead of trying again.
        var h = new Harness(RemotePrReviewLedger.Unavailable("The provider request failed."));
        var service = h.Build();

        var reply = await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id);
        var resolve = await service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id);

        Assert.False(reply.Ok);
        Assert.False(resolve.Ok);
        Assert.Contains("provider request failed", reply.Message);
        Assert.Contains("provider request failed", resolve.Message);
        Assert.DoesNotContain("No comment with id", reply.Message);
        h.Remote.Verify(r => r.ReplyToReviewThreadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        h.Remote.Verify(r => r.ResolveReviewThreadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_pull_request_url_no_provider_can_be_resolved_from_is_an_answer()
    {
        var h = new Harness(Ledger(Inline("11")));
        h.Run.PrUrl = "not-a-url";
        var service = h.Build();

        Assert.Empty((await service.ReadAsync("wi-1", null, h.Run.Id)).Items);
        Assert.False((await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id)).Ok);
        Assert.False((await service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id)).Ok);
    }
}
