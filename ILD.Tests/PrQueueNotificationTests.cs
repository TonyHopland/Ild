using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A person can only drop a queued answer they can see, and the window to do it
/// runs from the moment an agent queues until the round reaches the PR node —
/// all of it while the run is mid-flight. Nothing else refreshes the run in that
/// window, so every change to the queue has to say so.
/// </summary>
public class PrQueueNotificationTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RemotePrReviewLedger Ledger()
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", "body", Head, DateTime.UtcNow, "Copilot", false) },
            new[]
            {
                new RemotePrReviewItem("review", "11", "PRRT_11", "r1", "src/A.cs", 10,
                    "this allocation is wrong", "Copilot", Head, DateTime.UtcNow, false, false),
            },
            Head, null);

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<IRunNotifier> Notifier { get; } = new();
        public List<Guid> Notified { get; } = new();

        public Harness()
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
            Runs.Setup(s => s.GetPrCommentQueueAsync(Run.Id)).ReturnsAsync(() => Run.PrCommentQueue);
            Runs.Setup(s => s.TrySetPrCommentQueueAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentQueue, StringComparison.Ordinal)) return false;
                    Run.PrCommentQueue = json;
                    return true;
                });
            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(Ledger());
            Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);
            Notifier.Setup(n => n.PrQueueChangedAsync(It.IsAny<Guid>()))
                .Callback<Guid>(Notified.Add)
                .Returns(Task.CompletedTask);
        }

        public PrReviewService Build() => new(Runs.Object, Remote.Object, Notifier.Object);
    }

    [Fact]
    public async Task Queueing_an_answer_says_the_queue_changed()
    {
        var h = new Harness();

        var queued = await h.Build().ReplyAsync("wi-1", "11", "Answered.", h.Run.Id);

        Assert.True(queued.Ok);
        Assert.Equal(h.Run.Id, Assert.Single(h.Notified));
    }

    [Fact]
    public async Task Dropping_one_says_the_queue_changed()
    {
        var h = new Harness();
        var service = h.Build();
        Assert.True((await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id)).Ok);
        var write = Assert.Single(PrCommentQueueJson.TryParse(h.Run.PrCommentQueue));
        h.Notified.Clear();

        Assert.True(await service.DropQueuedAsync(h.Run.Id, write.Id));

        Assert.Equal(h.Run.Id, Assert.Single(h.Notified));
    }

    [Fact]
    public async Task A_drop_that_changes_nothing_says_nothing()
    {
        // The id is not in the queue, so no write happened and there is nothing
        // for anyone to re-read.
        var h = new Harness();
        var service = h.Build();
        Assert.True((await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id)).Ok);
        h.Notified.Clear();

        Assert.False(await service.DropQueuedAsync(h.Run.Id, "no-such-write"));

        Assert.Empty(h.Notified);
    }

    [Fact]
    public async Task A_read_of_the_review_says_nothing()
    {
        // Reading touches no queue. Announcing it would send every watching
        // client back to the server for a row that has not moved.
        var h = new Harness();

        await h.Build().ReadAsync("wi-1", null, h.Run.Id);

        Assert.Empty(h.Notified);
    }

    [Fact]
    public async Task The_service_still_works_with_no_notifier_at_all()
    {
        // The parameter is optional so a test can build the service with the two
        // collaborators it is really about; queuing must not depend on it.
        var h = new Harness();
        var service = new PrReviewService(h.Runs.Object, h.Remote.Object);

        Assert.True((await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id)).Ok);
        Assert.Single(PrCommentQueueJson.TryParse(h.Run.PrCommentQueue));
    }
}
