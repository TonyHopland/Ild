using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The agent-facing half of a review: read the whole ledger, answer a thread,
/// close it. The forge credentials stay here, as they do for <c>get_ci_log</c>,
/// and an id the work item's own pull request does not hold is refused rather
/// than passed through to the provider.
/// </summary>
public class PrReviewServiceTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string HeadA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HeadB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static RemotePrReviewItem Inline(
        string id, string path = "src/A.cs", int line = 10, string body = "this allocation is wrong",
        string author = "Copilot", string reviewId = "r2", string? threadId = null, string commit = HeadB,
        bool resolved = false)
        => new("review", id, threadId ?? $"PRRT_{id}", reviewId, path, line, body, author, commit,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), resolved, false);

    private static RemotePrReviewItem Suppressed(string path, int line, string body, string reviewId = "r1", string commit = HeadA)
        => new("suppressed", null, null, reviewId, path, line, body, "Copilot", commit,
            new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewItem Issue(string id, string body, string author = "tony", string commit = HeadB)
        => new("issue", id, null, null, null, null, body, author, commit,
            new DateTime(2026, 9, 19, 12, 30, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewSummary Review(string id, string head, bool incomplete = false)
        => new(id, "COMMENTED", "review body", head, new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Utc), "Copilot", incomplete);

    private static RemotePrReviewLedger Ledger(params RemotePrReviewItem[] items)
        => new(new[] { Review("r1", HeadA), Review("r2", HeadB) }, items, HeadB, null);

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public string? RecordedLedger { get; private set; }
        public int LedgerWrites { get; private set; }
        public string? RecordedQueue { get; private set; }

        public Harness(RemotePrReviewLedger? ledger, bool parkedAtPrNode = false, string? prUrl = PrUrl)
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-1",
                PrUrl = prUrl,
                Status = parkedAtPrNode ? LoopRunStatus.WaitingHuman : LoopRunStatus.Running,
                HumanFeedbackReason = parkedAtPrNode ? HumanFeedbackReasons.PrAwaitingMerge : null,
            };
            Runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(Run);
            Runs.Setup(s => s.GetByIdAsync(Run.Id)).ReturnsAsync(() => Run);
            Runs.Setup(s => s.SetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => { RecordedLedger = json; LedgerWrites++; })
                .Returns(Task.CompletedTask);
            // The queue is mutated by compare-and-set: the service reads the
            // column, then writes only if it still holds what it read.
            Runs.Setup(s => s.GetPrCommentQueueAsync(Run.Id)).ReturnsAsync(() => Run.PrCommentQueue);
            Runs.Setup(s => s.TrySetPrCommentQueueAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentQueue, StringComparison.Ordinal)) return false;
                    RecordedQueue = json;
                    Run.PrCommentQueue = json;
                    return true;
                });
            if (ledger is not null)
                Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(ledger);
        }

        public PrReviewService Build() => new(Runs.Object, Remote.Object);
    }

    [Fact]
    public async Task Returns_every_item_on_the_work_items_pull_request()
    {
        var h = new Harness(Ledger(
            Inline("4049159495"),
            Suppressed("frontend/src/utils/attachments.ts", 30, "preserve the original text"),
            Issue("4051372317", "and a note at the pull request level")));

        var ledger = await h.Build().ReadAsync("wi-1", sinceCommit: null, callerRunId: null);

        Assert.Equal(3, ledger.Items.Count);
        Assert.Equal(2, ledger.Reviews.Count);
        Assert.Equal(HeadB, ledger.HeadSha);
        h.Remote.Verify(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7"), Times.Once);
    }

    [Fact]
    public async Task What_ild_wrote_is_flagged_so_an_agent_does_not_answer_itself()
    {
        var h = new Harness(Ledger(
            Issue("500", PrCommentMarker.Stamp("Answered every point.", Guid.NewGuid())),
            Issue("501", "and a person's reply")));

        var ledger = await h.Build().ReadAsync("wi-1", sinceCommit: null, callerRunId: null);

        Assert.True(ledger.Items.Single(i => i.CommentId == "500").PostedByIld);
        Assert.False(ledger.Items.Single(i => i.CommentId == "501").PostedByIld);
    }

    [Fact]
    public async Task Naming_a_commit_asks_only_for_what_is_new_since_it()
    {
        // A round that already answered the review on HeadA needs to know what
        // the review on HeadB added, not the whole history again.
        var h = new Harness(Ledger(
            Inline("11", commit: HeadA, reviewId: "r1"),
            Suppressed("src/B.cs", 20, "an older hidden finding", reviewId: "r1", commit: HeadA),
            Inline("21", commit: HeadB, reviewId: "r2")));

        var ledger = await h.Build().ReadAsync("wi-1", sinceCommit: HeadA, callerRunId: null);

        var item = Assert.Single(ledger.Items);
        Assert.Equal("21", item.CommentId);
    }

    [Fact]
    public async Task The_round_that_acts_on_the_items_consumes_them()
    {
        // Under a changes-requested review on_rejected outranks on_comment every
        // tick, so that round reads the comments through this tool — and what it
        // was handed must not start a round of its own afterwards.
        var fetched = Ledger(Inline("4049159495"), Issue("4051372317", "and a note"));
        var h = new Harness(fetched);

        await h.Build().ReadAsync("wi-1", sinceCommit: null, callerRunId: h.Run.Id);

        Assert.Equal(1, h.LedgerWrites);
        Assert.Empty(PrCommentDelivery.Decide(fetched, HeadB, PrCommentLedgerJson.TryParse(h.RecordedLedger)).Items);
    }

    [Fact]
    public async Task A_read_while_the_run_is_parked_at_its_pr_node_consumes_nothing()
    {
        // A chat agent asked "what did the review say?" borrows the same tools;
        // consuming here would silently cancel the firing those items are about
        // to cause.
        var h = new Harness(Ledger(Inline("4049159495")), parkedAtPrNode: true);

        await h.Build().ReadAsync("wi-1", sinceCommit: null, callerRunId: h.Run.Id);

        Assert.Equal(0, h.LedgerWrites);
    }

    [Fact]
    public async Task A_read_from_somewhere_that_is_not_the_items_own_run_consumes_nothing()
    {
        var h = new Harness(Ledger(Inline("4049159495")));

        await h.Build().ReadAsync("wi-1", sinceCommit: null, callerRunId: null);
        await h.Build().ReadAsync("wi-1", sinceCommit: null, callerRunId: Guid.NewGuid());

        Assert.Equal(0, h.LedgerWrites);
    }

    [Fact]
    public async Task A_reply_is_queued_against_the_run_and_nothing_reaches_the_forge()
    {
        // An agent never writes to a pull request. The answer is recorded and
        // the PR node sends it at the end of the round, which is the whole
        // point: a human sits between the agent and anything public — including
        // a chat agent, which has no loop run of its own driving it.
        var h = new Harness(Ledger(Inline("4049159495", threadId: "PRRT_thread_1")));

        var result = await h.Build().ReplyAsync("wi-1", "4049159495", "That compiles: C# allows a long array length.", h.Run.Id);

        Assert.True(result.Ok);
        Assert.Contains("Queued", result.Message, StringComparison.OrdinalIgnoreCase);
        h.Remote.Verify(
            r => r.ReplyToReviewThreadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

        var queued = Assert.Single(PrCommentQueueJson.TryParse(h.RecordedQueue));
        Assert.Equal(PrQueuedWrite.Reply, queued.Kind);
        Assert.Equal("4049159495", queued.TargetId);
        Assert.Equal("That compiles: C# allows a long array length.", queued.Body);
        // Where it would land, so the queue reads without opening the transcript.
        Assert.Equal("src/A.cs", queued.Path);
        Assert.Equal(10, queued.Line);
    }

    [Fact]
    public async Task Queuing_a_reply_records_nothing_in_the_ledger_until_it_is_actually_posted()
    {
        // The marker and the posted id belong where the post happens. A reply a
        // human drops before the PR node runs must never have been recorded as
        // something ILD wrote.
        var h = new Harness(Ledger(Inline("4049159495")));

        await h.Build().ReplyAsync("wi-1", "4049159495", "Answered.", h.Run.Id);

        Assert.Null(h.RecordedLedger);
        Assert.Equal(0, h.LedgerWrites);
    }

    [Fact]
    public async Task A_queued_write_a_human_drops_is_gone_and_nothing_else_is()
    {
        var h = new Harness(Ledger(Inline("4049159495", threadId: "PRRT_thread_1")));
        h.Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);
        var service = h.Build();
        var reply = await service.ReplyAsync("wi-1", "4049159495", "Answered.", h.Run.Id);
        await service.ResolveAsync("wi-1", "PRRT_thread_1", h.Run.Id);
        Assert.Equal(2, PrCommentQueueJson.TryParse(h.RecordedQueue).Count);

        Assert.True(await service.DropQueuedAsync(h.Run.Id, reply.Id!));

        var left = Assert.Single(PrCommentQueueJson.TryParse(h.RecordedQueue));
        Assert.Equal(PrQueuedWrite.Resolve, left.Kind);
        Assert.False(await service.DropQueuedAsync(h.Run.Id, "never-queued"));
    }

    [Fact]
    public async Task A_reply_to_a_comment_this_pull_request_does_not_hold_is_refused_by_name()
    {
        var h = new Harness(Ledger(Inline("4049159495")));

        var result = await h.Build().ReplyAsync("wi-1", "999999", "hello elsewhere", h.Run.Id);

        Assert.False(result.Ok);
        Assert.Contains("999999", result.Message);
        h.Remote.Verify(r => r.ReplyToReviewThreadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_resolve_is_queued_too_and_closes_nothing_yet()
    {
        var h = new Harness(Ledger(Inline("4049159495", threadId: "PRRT_thread_1")));
        h.Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);

        var result = await h.Build().ResolveAsync("wi-1", "PRRT_thread_1", h.Run.Id);

        Assert.True(result.Ok);
        Assert.Contains("Queued", result.Message, StringComparison.OrdinalIgnoreCase);
        h.Remote.Verify(
            r => r.ResolveReviewThreadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        var queued = Assert.Single(PrCommentQueueJson.TryParse(h.RecordedQueue));
        Assert.Equal(PrQueuedWrite.Resolve, queued.Kind);
        Assert.Equal("PRRT_thread_1", queued.TargetId);
    }

    [Fact]
    public async Task Resolving_a_thread_this_pull_request_does_not_hold_is_refused_by_name()
    {
        var h = new Harness(Ledger(Inline("4049159495", threadId: "PRRT_thread_1")));

        var result = await h.Build().ResolveAsync("wi-1", "PRRT_somewhere_else", h.Run.Id);

        Assert.False(result.Ok);
        Assert.Contains("PRRT_somewhere_else", result.Message);
        h.Remote.Verify(r => r.ResolveReviewThreadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_provider_that_cannot_resolve_threads_answers_rather_than_claiming_success()
    {
        // Queued, the refusal would only surface when the PR node tried — long
        // after the agent stopped listening. A provider that can never resolve
        // is asked before anything is queued, so the answer arrives in the
        // agent's hands and nothing is left waiting that cannot happen.
        var h = new Harness(Ledger(Inline("4049159495", threadId: "PRRT_thread_1")));
        h.Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(false);

        var result = await h.Build().ResolveAsync("wi-1", "PRRT_thread_1", h.Run.Id);

        Assert.False(result.Ok);
        Assert.Contains("not supported", result.Message);
        Assert.Null(h.RecordedQueue);
    }

    [Fact]
    public async Task A_work_item_with_no_pull_request_gets_a_message_not_an_error()
    {
        var h = new Harness(ledger: null, prUrl: null);
        var service = h.Build();

        var ledger = await service.ReadAsync("wi-1", null, h.Run.Id);
        Assert.Empty(ledger.Items);
        Assert.Contains("no pull request", ledger.Message);

        Assert.False((await service.ReplyAsync("wi-1", "1", "hi", h.Run.Id)).Ok);
        Assert.False((await service.ResolveAsync("wi-1", "t1", h.Run.Id)).Ok);
    }

    [Fact]
    public async Task A_work_item_with_no_run_at_all_is_an_answer_not_a_crash()
    {
        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync((LoopRun?)null);
        var service = new PrReviewService(runs.Object, new Mock<IRemoteProvider>().Object);

        Assert.Empty((await service.ReadAsync("wi-1", null, null)).Items);
        Assert.False((await service.ReplyAsync("wi-1", "1", "hi", null)).Ok);
        Assert.False((await service.ResolveAsync("wi-1", "t1", null)).Ok);
    }
}
