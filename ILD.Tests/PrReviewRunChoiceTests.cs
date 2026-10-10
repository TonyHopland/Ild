using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Reading a work item's review shows the PR of the run the item is showing,
/// even once that run has ended. Queuing a write on it needs a live run: a
/// write waits for the PR node of the run it was queued on, and an ended run
/// has no PR node left to send it.
/// </summary>
public class PrReviewRunChoiceTests
{
    private const string PrUrl = "https://forge.example.test/team/repo/pulls/7";
    private const string RepoUrl = "https://forge.example.test/team/repo";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RemotePrReviewLedger Fetched()
        => new(
            Array.Empty<RemotePrReviewSummary>(),
            new[]
            {
                new RemotePrReviewItem("issue", "127", null, null, null, null,
                    "Also change the Readme to have the project name", "reviewer", Head, DateTime.UtcNow, false, false),
            },
            Head, null);

    [Theory]
    [InlineData(LoopRunStatus.Completed)]
    [InlineData(LoopRunStatus.Failed)]
    [InlineData(LoopRunStatus.Cancelled)]
    public async Task The_review_of_an_ended_run_can_be_read_but_nothing_is_queued_on_it(LoopRunStatus ended)
    {
        using var db = new TestDb();
        var item = "WI-" + Guid.NewGuid().ToString("N");
        var run = RunTimeline.SeedRun(db, item, RunTimeline.SeedVersion(db), ended);
        db.Context.LoopRuns.Single(r => r.Id == run.Id).PrUrl = PrUrl;
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(Fetched());
        var service = new PrReviewService(db.LoopRuns, remote.Object);

        var read = await service.ReadAsync(item, null, null);
        var comment = await service.CommentAsync(item, "Renamed it.", null, Guid.NewGuid());
        var reply = await service.ReplyAsync(item, "127", "Renamed it.", null, Guid.NewGuid());

        Assert.True(string.IsNullOrEmpty(read.Message), read.Message);
        Assert.Contains(read.Items, i => i.CommentId == "127");
        Assert.False(comment.Ok);
        Assert.False(reply.Ok);
        Assert.Null(db.Fresh().LoopRuns.AsNoTracking().Single(r => r.Id == run.Id).PrCommentQueue);
    }
}
