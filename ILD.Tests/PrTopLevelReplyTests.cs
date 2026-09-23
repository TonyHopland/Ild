using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Most human feedback is a comment on the pull request itself, not on a line.
/// A forge threads only inline comments, so the answer to one is a new comment
/// that quotes what it answers — refusing to answer at all left the commonest
/// kind of feedback with nothing but the generic reply the round posts anyway.
/// </summary>
public class PrTopLevelReplyTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RemotePrReviewLedger Fetched()
        => new(
            new[]
            {
                new RemotePrReviewSummary(
                    "r1", "COMMENTED", "Some improvements to be made", Head, DateTime.UtcNow, "tony", false),
            },
            new[]
            {
                new RemotePrReviewItem("issue", "127", null, null, null, null,
                    "Also change the Readme to have the project name", "tony", Head, DateTime.UtcNow, false, false),
                new RemotePrReviewItem("body", null, null, "r1", null, null,
                    "Some improvements to be made", "tony", Head, DateTime.UtcNow, false, false),
                new RemotePrReviewItem("suppressed", null, null, "r1", "src/A.cs", 10,
                    "a finding with no comment of its own", "Copilot", Head, DateTime.UtcNow, false, false),
            },
            Head, null);

    private static (PrReviewService Service, LoopRun Run) Build()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "wi-1", PrUrl = PrUrl, Status = LoopRunStatus.Running,
        };
        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(run);
        runs.Setup(s => s.GetByIdAsync(run.Id)).ReturnsAsync(() => run);
        runs.Setup(s => s.GetPrCommentQueueAsync(run.Id)).ReturnsAsync(() => run.PrCommentQueue);
        runs.Setup(s => s.TrySetPrCommentQueueAsync(run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) =>
            {
                if (!string.Equals(expected, run.PrCommentQueue, StringComparison.Ordinal)) return false;
                run.PrCommentQueue = json;
                return true;
            });

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(Fetched());
        return (new PrReviewService(runs.Object, remote.Object), run);
    }

    [Fact]
    public async Task A_top_level_comment_is_answered_by_a_comment_that_quotes_it()
    {
        var (service, run) = Build();

        var queued = await service.ReplyAsync("wi-1", "127", "Renamed it.", run.Id);

        Assert.True(queued.Ok);
        var write = Assert.Single(PrCommentQueueJson.TryParse(run.PrCommentQueue));
        Assert.Equal(PrQueuedWrite.Comment, write.Kind);
        Assert.Contains("> Also change the Readme", write.Body!, StringComparison.Ordinal);
        Assert.Contains("Renamed it.", write.Body!, StringComparison.Ordinal);
        Assert.Contains("127", write.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_body_is_answered_by_its_review_id()
    {
        var (service, run) = Build();

        var queued = await service.ReplyAsync("wi-1", "r1", "Understood.", run.Id);

        Assert.True(queued.Ok);
        var write = Assert.Single(PrCommentQueueJson.TryParse(run.PrCommentQueue));
        Assert.Equal(PrQueuedWrite.Comment, write.Kind);
        Assert.Contains("> Some improvements to be made", write.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inline_comment_still_answers_on_its_own_thread()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "wi-1", PrUrl = PrUrl, Status = LoopRunStatus.Running,
        };
        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(run);
        runs.Setup(s => s.GetPrCommentQueueAsync(run.Id)).ReturnsAsync(() => run.PrCommentQueue);
        runs.Setup(s => s.TrySetPrCommentQueueAsync(run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) =>
            {
                if (!string.Equals(expected, run.PrCommentQueue, StringComparison.Ordinal)) return false;
                run.PrCommentQueue = json;
                return true;
            });
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(
            new RemotePrReviewLedger(
                Array.Empty<RemotePrReviewSummary>(),
                new[]
                {
                    new RemotePrReviewItem("review", "125", "PRRT_125", "r1", "README.md", 3,
                        "I dont like this, remove it", "tony", Head, DateTime.UtcNow, false, false),
                },
                Head, null));

        var queued = await new PrReviewService(runs.Object, remote.Object)
            .ReplyAsync("wi-1", "125", "Removed.", run.Id);

        Assert.True(queued.Ok);
        var write = Assert.Single(PrCommentQueueJson.TryParse(run.PrCommentQueue));
        Assert.Equal(PrQueuedWrite.Reply, write.Kind);
        // Unquoted: the thread already shows what is being answered.
        Assert.Equal("Removed.", write.Body);
    }

    [Fact]
    public async Task An_id_that_is_both_a_comment_and_a_review_answers_the_comment()
    {
        // Forgejo numbers reviews and comments from separate counters, so a
        // review id can equal a real comment id. Answering the body instead
        // would post a top-level comment and leave the inline thread open.
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "wi-1", PrUrl = PrUrl, Status = LoopRunStatus.Running,
        };
        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(run);
        runs.Setup(s => s.GetPrCommentQueueAsync(run.Id)).ReturnsAsync(() => run.PrCommentQueue);
        runs.Setup(s => s.TrySetPrCommentQueueAsync(run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) =>
            {
                if (!string.Equals(expected, run.PrCommentQueue, StringComparison.Ordinal)) return false;
                run.PrCommentQueue = json;
                return true;
            });
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(
            new RemotePrReviewLedger(
                Array.Empty<RemotePrReviewSummary>(),
                new[]
                {
                    // The body is listed first, as it always is.
                    new RemotePrReviewItem("body", null, null, "42", null, null,
                        "Some improvements to be made", "tony", Head, DateTime.UtcNow, false, false),
                    new RemotePrReviewItem("review", "42", "PRRT_42", "r9", "README.md", 3,
                        "I dont like this, remove it", "tony", Head, DateTime.UtcNow, false, false),
                },
                Head, null));

        var queued = await new PrReviewService(runs.Object, remote.Object)
            .ReplyAsync("wi-1", "42", "Removed.", run.Id);

        Assert.True(queued.Ok);
        var write = Assert.Single(PrCommentQueueJson.TryParse(run.PrCommentQueue));
        Assert.Equal(PrQueuedWrite.Reply, write.Kind);
        Assert.Equal("Removed.", write.Body);
    }
    [Fact]
    public async Task A_suppressed_finding_is_refused_and_says_where_to_answer_instead()
    {
        // The forge never gave it an id, so there is neither a thread nor a
        // comment to quote. The review body that carries it is the answerable
        // thing, and the refusal now says so.
        var (service, run) = Build();

        var refused = await service.ReplyAsync("wi-1", "no-such-id", "…", run.Id);

        Assert.False(refused.Ok);
        Assert.Null(run.PrCommentQueue);
    }
}
