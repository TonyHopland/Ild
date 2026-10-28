using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// An agent correcting what it has queued for the pull request, before the PR
/// node sends it. On WI-181 a human corrected the implementer twice in one
/// round, each correction came out as another reply to the same review, and
/// three answers — two of them no longer true — sat in the queue waiting to be
/// posted. Answering an item again now replaces the caller's own pending answer,
/// and a write it no longer wants can be withdrawn.
///
/// All of it is limited to the caller's OWN pending writes: a write another run,
/// a chat session or a human wants posted stays out of an agent's reach, which
/// is why the human drop is still a separate surface.
/// </summary>
public class PrOwnQueuedWriteTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime Written = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private static RemotePrReviewItem Inline(string id)
        => new("review", id, $"PRRT_{id}", "r1", "src/A.cs", 10, $"finding {id}", "Copilot", Head, Written, false, false);

    private static readonly RemotePrReviewItem TopLevel =
        new("issue", "21", null, null, null, null, "why this approach?", "alice", Head, Written, false, false);

    private static readonly RemotePrReviewItem ReviewBody =
        new("body", null, null, "r1", null, null, "overall this needs another pass", "Copilot", Head, Written, false, false);

    private static RemotePrReviewLedger Fetched()
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", ReviewBody.Body, Head, Written, "Copilot", false) },
            new[] { Inline("11"), Inline("12"), TopLevel, ReviewBody },
            Head, null);

    /// <summary>A queue written before writes recorded who queued them or which item they answer.</summary>
    private const string LegacyQueue =
        """[{"id":"legacy000001","kind":"reply","targetId":"11","body":"an answer from before","path":"src/A.cs","line":10,"queuedAt":"2026-09-20T12:00:00Z"}]""";

    private sealed class Harness : IDisposable
    {
        public TestDb Db { get; } = new();
        public LoopRun Run { get; private set; } = null!;
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<IRunNotifier> Notifier { get; } = new();
        public Mock<IEventLogService> Events { get; } = new();
        public List<EventLog> Logged { get; } = new();
        public List<Guid> Notified { get; } = new();

        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            h.Run = await h.AddRunAsync("wi-1");
            return h;
        }

        private Harness()
        {
            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(Fetched());
            Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);

            Notifier.Setup(n => n.PrQueueChangedAsync(It.IsAny<Guid>()))
                .Callback<Guid>(Notified.Add)
                .Returns(Task.CompletedTask);
            Events.Setup(s => s.AppendAsync(It.IsAny<Guid>(), It.IsAny<EventType>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
                .Callback<Guid, EventType, string, Guid?, Guid?, string?>((run, type, data, node, runNode, edge) => Logged.Add(
                    new EventLog { LoopRunId = run, EventType = type, Data = data, NodeId = node, RunNodeId = runNode, EdgeName = edge }))
                .ReturnsAsync(1);
        }

        public Task<LoopRun> AddRunAsync(string workItemId) => PrRunSeed.SeedAsync(Db, workItemId: workItemId);

        /// <summary>A service on its own store instance, as each HTTP request gets.</summary>
        public PrReviewService Service
            => new(new LoopRunStore(Db.Fresh()), Remote.Object, Notifier.Object, Events.Object);

        public string? QueueJson => Db.Fresh().LoopRuns.AsNoTracking().Where(r => r.Id == Run.Id).Select(r => r.PrCommentQueue).Single();
        public string? LedgerJson => Db.Fresh().LoopRuns.AsNoTracking().Where(r => r.Id == Run.Id).Select(r => r.PrCommentLedger).Single();

        public IReadOnlyList<PrQueuedWrite> Queue => PrCommentQueueJson.TryParse(QueueJson);

        public Dictionary<Guid, (string? Queue, string? Ledger)> Columns()
            => Db.Fresh().LoopRuns.AsNoTracking()
                .Select(r => new { r.Id, r.PrCommentQueue, r.PrCommentLedger })
                .AsEnumerable()
                .ToDictionary(r => r.Id, r => (r.PrCommentQueue, r.PrCommentLedger));

        /// <summary>Forget everything the setup steps of a test caused, so only the step under test is observed.</summary>
        public void ForgetSetup()
        {
            Logged.Clear();
            Notified.Clear();
            Remote.Invocations.Clear();
        }

        public void Dispose() => Db.Dispose();
    }

    private static IEnumerable<EventLog> Of(Harness h, EventType type) => h.Logged.Where(e => e.EventType == type);

    private static void AssertNamesCaller(string data, Guid caller)
        => Assert.True(
            data.Contains(caller.ToString(), StringComparison.OrdinalIgnoreCase)
                || data.Contains(caller.ToString("N"), StringComparison.OrdinalIgnoreCase),
            $"the event does not name the caller {caller}:\n{data}");

    // -- Replacing ------------------------------------------------------------

    [Fact]
    public async Task Answering_the_same_inline_comment_three_times_leaves_only_the_last_answer_under_the_first_id()
    {
        using var h = await Harness.CreateAsync();

        var first = await h.Service.ReplyAsync("wi-1", "11", "first answer", h.Run.Id);
        var second = await h.Service.ReplyAsync("wi-1", "11", "second answer", h.Run.Id);
        var third = await h.Service.ReplyAsync("wi-1", "11", "third answer", h.Run.Id);

        Assert.True(first.Ok);
        Assert.NotNull(first.Id);
        foreach (var later in new[] { second, third })
        {
            Assert.True(later.Ok);
            Assert.Equal(first.Id, later.Id);
            Assert.Contains("replaced", later.Message!, StringComparison.OrdinalIgnoreCase);
        }

        var write = Assert.Single(h.Queue);
        Assert.Equal(first.Id, write.Id);
        Assert.Equal(PrQueuedWrite.Reply, write.Kind);
        Assert.Equal("11", write.TargetId);
        Assert.Equal("third answer", write.Body);
    }

    [Theory]
    [InlineData("21")] // a top-level comment
    [InlineData("r1")] // a review body, named by its review id
    public async Task Answering_an_item_with_no_thread_again_replaces_the_quoted_answer(string itemId)
    {
        using var h = await Harness.CreateAsync();

        var first = await h.Service.ReplyAsync("wi-1", itemId, "first answer", h.Run.Id);
        await h.Service.ReplyAsync("wi-1", itemId, "second answer", h.Run.Id);
        var last = await h.Service.ReplyAsync("wi-1", itemId, "third answer", h.Run.Id);

        Assert.True(last.Ok);
        Assert.Equal(first.Id, last.Id);
        Assert.Contains("replaced", last.Message!, StringComparison.OrdinalIgnoreCase);
        var write = Assert.Single(h.Queue);
        Assert.Equal(first.Id, write.Id);
        Assert.Equal(PrQueuedWrite.Comment, write.Kind);
        Assert.Equal(itemId, write.TargetId);
        Assert.Contains("third answer", write.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("first answer", write.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("second answer", write.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_an_item_just_answered_turns_the_answer_into_a_resolve_under_the_same_id()
    {
        using var h = await Harness.CreateAsync();
        var reply = await h.Service.ReplyAsync("wi-1", "11", "an answer I no longer stand by", h.Run.Id);

        var closed = await h.Service.CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        Assert.True(closed.Ok);
        Assert.Equal(reply.Id, closed.Id);
        Assert.Contains("replaced", closed.Message!, StringComparison.OrdinalIgnoreCase);
        var write = Assert.Single(h.Queue);
        Assert.Equal(reply.Id, write.Id);
        Assert.Equal(PrQueuedWrite.Resolve, write.Kind);
        Assert.Equal("PRRT_11", write.TargetId);
        Assert.Single(Of(h, EventType.PrReviewItemClosed));
    }

    [Fact]
    public async Task Answering_an_item_just_closed_turns_the_resolve_into_the_answer_under_the_same_id()
    {
        using var h = await Harness.CreateAsync();
        var closed = await h.Service.CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        var reply = await h.Service.ReplyAsync("wi-1", "11", "on reflection, here is why", h.Run.Id);

        Assert.True(reply.Ok);
        Assert.Equal(closed.Id, reply.Id);
        var write = Assert.Single(h.Queue);
        Assert.Equal(closed.Id, write.Id);
        Assert.Equal(PrQueuedWrite.Reply, write.Kind);
        Assert.Equal("11", write.TargetId);
        Assert.Equal("on reflection, here is why", write.Body);
    }

    [Fact]
    public async Task Closing_an_answered_item_without_resolving_takes_the_answer_back_and_puts_nothing_back()
    {
        using var h = await Harness.CreateAsync();
        var delivered = PrCommentLedger.Empty with
        {
            Head = Head,
            DeliveredIds = new[] { PrCommentLedger.KeyFor("review", "11") },
            DeliveredHashes = new[] { PrCommentLedger.Fingerprint("src/A.cs", 10, "finding 11") },
        };
        await PrRunSeed.SetLedgerAsync(h.Db, h.Run.Id, PrCommentLedgerJson.Serialize(delivered));
        var reply = await h.Service.ReplyAsync("wi-1", "11", "an answer I no longer stand by", h.Run.Id);
        var ledgerBefore = h.LedgerJson;
        h.ForgetSetup();

        var closed = await h.Service.CloseAsync("wi-1", "11", resolve: false, h.Run.Id);

        Assert.True(closed.Ok);
        Assert.Contains("withdr", closed.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Queue);
        Assert.Single(Of(h, EventType.PrReviewItemClosed));
        var withdrawn = Assert.Single(Of(h, EventType.PrQueuedWriteWithdrawn));
        Assert.Equal(h.Run.Id, withdrawn.LoopRunId);
        Assert.Contains(reply.Id!, withdrawn.Data!, StringComparison.Ordinal);
        Assert.Contains("an answer I no longer stand by", withdrawn.Data!, StringComparison.Ordinal);
        // Closing is a judgement on the item, not a lost answer: the finding
        // stays handed over rather than being put back within reach.
        Assert.Equal(ledgerBefore, h.LedgerJson);
    }

    [Fact]
    public async Task Resolving_a_thread_already_waiting_to_be_resolved_queues_nothing_more()
    {
        using var h = await Harness.CreateAsync();
        var first = await h.Service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id);
        var queueBefore = h.QueueJson;
        h.ForgetSetup();

        var again = await h.Service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id);

        Assert.True(again.Ok);
        Assert.Equal(first.Id, again.Id);
        Assert.Contains("already", again.Message!, StringComparison.OrdinalIgnoreCase);
        var write = Assert.Single(h.Queue);
        Assert.Equal(first.Id, write.Id);
        Assert.Equal(queueBefore, h.QueueJson);
        Assert.Empty(h.Notified);
        Assert.Empty(h.Logged);
    }

    [Fact]
    public async Task A_thread_a_close_is_already_resolving_is_not_resolved_twice()
    {
        using var h = await Harness.CreateAsync();
        var closed = await h.Service.CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        var resolved = await h.Service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id);

        Assert.True(resolved.Ok);
        Assert.Equal(closed.Id, resolved.Id);
        var write = Assert.Single(h.Queue);
        Assert.Equal(closed.Id, write.Id);
        Assert.Equal(PrQueuedWrite.Resolve, write.Kind);
    }

    [Fact]
    public async Task Closing_with_resolve_a_thread_already_waiting_to_be_resolved_adds_no_second_resolve()
    {
        using var h = await Harness.CreateAsync();
        await h.Service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id);

        var closed = await h.Service.CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        Assert.True(closed.Ok);
        var write = Assert.Single(h.Queue);
        Assert.Equal(PrQueuedWrite.Resolve, write.Kind);
        Assert.Equal("PRRT_11", write.TargetId);
    }

    [Fact]
    public async Task Closing_an_answered_item_whose_thread_is_already_being_resolved_leaves_one_resolve_and_no_answer()
    {
        using var h = await Harness.CreateAsync();
        await h.Service.ReplyAsync("wi-1", "11", "an answer I no longer stand by", h.Run.Id);
        await h.Service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id);

        var closed = await h.Service.CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        Assert.True(closed.Ok);
        var write = Assert.Single(h.Queue);
        Assert.Equal(PrQueuedWrite.Resolve, write.Kind);
        Assert.Equal("PRRT_11", write.TargetId);
    }

    [Fact]
    public async Task Replying_and_resolving_the_same_thread_still_queues_both()
    {
        // The documented flow — answer, then close the thread — is not a
        // correction, in either order.
        using var h = await Harness.CreateAsync();

        await h.Service.ReplyAsync("wi-1", "11", "answered", h.Run.Id);
        await h.Service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id);
        await h.Service.ResolveAsync("wi-1", "PRRT_12", h.Run.Id);
        await h.Service.ReplyAsync("wi-1", "12", "answered too", h.Run.Id);

        Assert.Equal(
            new[]
            {
                (PrQueuedWrite.Reply, "11"), (PrQueuedWrite.Resolve, "PRRT_11"),
                (PrQueuedWrite.Resolve, "PRRT_12"), (PrQueuedWrite.Reply, "12"),
            },
            h.Queue.Select(w => (w.Kind, w.TargetId)).ToArray());
    }

    [Theory]
    [InlineData("another run")]
    [InlineData("a chat session")]
    [InlineData("a queue from before owners were recorded")]
    public async Task An_answer_someone_else_queued_is_answered_beside_never_replaced(string queuedBy)
    {
        using var h = await Harness.CreateAsync();
        string theirs;
        switch (queuedBy)
        {
            case "another run":
                theirs = (await h.Service.ReplyAsync("wi-1", "11", "their answer", Guid.NewGuid())).Id!;
                break;
            case "a chat session":
                theirs = (await h.Service.ReplyAsync("wi-1", "11", "their answer", null, Guid.NewGuid())).Id!;
                break;
            default:
                await PrRunSeed.SetQueueAsync(h.Db, h.Run.Id, LegacyQueue);
                theirs = "legacy000001";
                break;
        }
        var theirBody = Assert.Single(h.Queue).Body;

        var mine = await h.Service.ReplyAsync("wi-1", "11", "my answer", h.Run.Id);

        Assert.True(mine.Ok);
        Assert.NotEqual(theirs, mine.Id);
        Assert.Equal(2, h.Queue.Count);
        Assert.Equal(theirBody, h.Queue.Single(w => w.Id == theirs).Body);
        Assert.Equal("my answer", h.Queue.Single(w => w.Id == mine.Id).Body);
        Assert.Empty(Of(h, EventType.PrQueuedWriteReplaced));
    }

    [Fact]
    public async Task A_chat_session_answering_again_replaces_its_own_answer_and_never_a_runs()
    {
        using var h = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var runs = await h.Service.ReplyAsync("wi-1", "11", "the round's answer", h.Run.Id);

        var first = await h.Service.ReplyAsync("wi-1", "11", "chat's first answer", null, chat);
        var second = await h.Service.ReplyAsync("wi-1", "11", "chat's second answer", null, chat);

        Assert.NotEqual(runs.Id, first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.Contains("replaced", second.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, h.Queue.Count);
        Assert.Equal("the round's answer", h.Queue.Single(w => w.Id == runs.Id).Body);
        Assert.Equal("chat's second answer", h.Queue.Single(w => w.Id == first.Id).Body);

        var replaced = Assert.Single(Of(h, EventType.PrQueuedWriteReplaced));
        AssertNamesCaller(replaced.Data!, chat);
    }

    [Fact]
    public async Task A_caller_that_says_it_is_a_run_is_that_run_and_not_also_its_chat_session()
    {
        // Run id wins, never both — as with work items an agent created.
        using var h = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var viaRun = await h.Service.ReplyAsync("wi-1", "11", "sent with both headers", h.Run.Id, chat);

        var viaChat = await h.Service.ReplyAsync("wi-1", "11", "sent as the chat session", null, chat);

        Assert.NotEqual(viaRun.Id, viaChat.Id);
        Assert.Equal(2, h.Queue.Count);
    }

    [Fact]
    public async Task A_caller_with_no_identity_owns_nothing_and_so_replaces_nothing()
    {
        using var h = await Harness.CreateAsync();

        var first = await h.Service.ReplyAsync("wi-1", "11", "first", null, null);
        var second = await h.Service.ReplyAsync("wi-1", "11", "second", null, null);

        Assert.True(second.Ok);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, h.Queue.Count);
    }

    [Fact]
    public async Task General_comments_about_the_round_are_never_merged()
    {
        using var h = await Harness.CreateAsync();

        var first = await h.Service.CommentAsync("wi-1", "Rebased onto main.", h.Run.Id);
        var second = await h.Service.CommentAsync("wi-1", "Re-ran the gate.", h.Run.Id);

        Assert.True(second.Ok);
        Assert.NotNull(first.Id);
        Assert.NotNull(second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(new[] { "Rebased onto main.", "Re-ran the gate." }, h.Queue.Select(w => w.Body).ToArray());
    }

    [Fact]
    public async Task A_full_queue_still_takes_a_replacement_but_nothing_new()
    {
        using var h = await Harness.CreateAsync();
        var reply = await h.Service.ReplyAsync("wi-1", "11", "first answer", h.Run.Id);
        for (var i = 1; i < PrQueuedWrite.MaxQueued; i++)
            Assert.True((await h.Service.CommentAsync("wi-1", $"note {i}", h.Run.Id)).Ok);

        var replaced = await h.Service.ReplyAsync("wi-1", "11", "corrected answer", h.Run.Id);
        var refused = await h.Service.CommentAsync("wi-1", "one too many", h.Run.Id);

        Assert.True(replaced.Ok);
        Assert.Equal(reply.Id, replaced.Id);
        Assert.False(refused.Ok);
        Assert.Equal(PrQueuedWrite.MaxQueued, h.Queue.Count);
        Assert.Equal("corrected answer", h.Queue.Single(w => w.Id == reply.Id).Body);
    }

    [Fact]
    public async Task Each_replacement_is_recorded_with_what_it_said_before_and_after()
    {
        using var h = await Harness.CreateAsync();
        var first = await h.Service.ReplyAsync("wi-1", "11", "first answer", h.Run.Id);

        await h.Service.ReplyAsync("wi-1", "11", "second answer", h.Run.Id);
        await h.Service.CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        var replaced = Of(h, EventType.PrQueuedWriteReplaced).ToList();
        Assert.Equal(2, replaced.Count);
        Assert.All(replaced, e =>
        {
            Assert.Equal(h.Run.Id, e.LoopRunId);
            Assert.Contains(first.Id!, e.Data!, StringComparison.Ordinal);
            AssertNamesCaller(e.Data!, h.Run.Id);
        });
        Assert.Contains("first answer", replaced[0].Data!, StringComparison.Ordinal);
        Assert.Contains("second answer", replaced[0].Data!, StringComparison.Ordinal);
        // A resolve has no body; it is described instead.
        Assert.Contains("second answer", replaced[1].Data!, StringComparison.Ordinal);
        Assert.Contains("PRRT_11", replaced[1].Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_event_store_that_will_not_take_the_record_costs_the_record_not_the_correction()
    {
        using var h = await Harness.CreateAsync();
        h.Events.Setup(s => s.AppendAsync(It.IsAny<Guid>(), It.IsAny<EventType>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>())).ThrowsAsync(new InvalidOperationException("down"));
        var first = await h.Service.ReplyAsync("wi-1", "11", "first answer", h.Run.Id);
        var gone = await h.Service.CommentAsync("wi-1", "never mind", h.Run.Id);

        var replaced = await h.Service.ReplyAsync("wi-1", "11", "second answer", h.Run.Id);
        var withdrawn = await h.Service.WithdrawAsync("wi-1", gone.Id!, h.Run.Id, null);

        Assert.True(replaced.Ok);
        Assert.True(withdrawn.Ok);
        var write = Assert.Single(h.Queue);
        Assert.Equal(first.Id, write.Id);
        Assert.Equal("second answer", write.Body);
    }

    // -- Withdrawing ----------------------------------------------------------

    [Theory]
    [InlineData(PrQueuedWrite.Reply)]
    [InlineData(PrQueuedWrite.Comment)]
    [InlineData(PrQueuedWrite.Resolve)]
    public async Task A_write_of_any_kind_the_caller_queued_can_be_taken_back(string kind)
    {
        using var h = await Harness.CreateAsync();
        var kept = await h.Service.CommentAsync("wi-1", "Something that should still go out.", h.Run.Id);
        var queued = kind switch
        {
            PrQueuedWrite.Reply => await h.Service.ReplyAsync("wi-1", "11", "an answer to take back", h.Run.Id),
            PrQueuedWrite.Comment => await h.Service.CommentAsync("wi-1", "a comment to take back", h.Run.Id),
            _ => await h.Service.ResolveAsync("wi-1", "PRRT_11", h.Run.Id),
        };
        h.ForgetSetup();

        var withdrawn = await h.Service.WithdrawAsync("wi-1", queued.Id!, h.Run.Id, null);

        Assert.True(withdrawn.Ok);
        Assert.Equal(queued.Id, withdrawn.Id);
        Assert.False(string.IsNullOrWhiteSpace(withdrawn.Message));
        var left = Assert.Single(h.Queue);
        Assert.Equal(kept.Id, left.Id);
        Assert.Equal(new[] { h.Run.Id }, h.Notified);

        var logged = Assert.Single(Of(h, EventType.PrQueuedWriteWithdrawn));
        Assert.Equal(h.Run.Id, logged.LoopRunId);
        Assert.Contains(queued.Id!, logged.Data!, StringComparison.Ordinal);
        AssertNamesCaller(logged.Data!, h.Run.Id);
        Assert.Contains(kind == PrQueuedWrite.Resolve ? "PRRT_11" : "to take back", logged.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chat_session_can_take_back_what_it_queued()
    {
        using var h = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var queued = await h.Service.ReplyAsync("wi-1", "11", "the chat's answer", null, chat);

        var withdrawn = await h.Service.WithdrawAsync("wi-1", queued.Id!, null, chat);

        Assert.True(withdrawn.Ok);
        Assert.Empty(h.Queue);
    }

    [Fact]
    public async Task Taking_back_an_answer_puts_its_finding_back_within_reach_as_a_human_drop_does()
    {
        var hash = PrCommentLedger.Fingerprint("src/A.cs", 10, "finding 11");
        using var h = await Harness.CreateAsync();
        await PrRunSeed.SetLedgerAsync(h.Db, h.Run.Id, PrCommentLedgerJson.Serialize(PrCommentLedger.Empty with
        {
            Head = Head,
            DeliveredIds = new[] { PrCommentLedger.KeyFor("review", "11") },
            DeliveredHashes = new[] { hash },
        }));
        var queued = await h.Service.ReplyAsync("wi-1", "11", "an answer to take back", h.Run.Id);
        Assert.Contains(hash, PrCommentLedgerJson.TryParse(h.LedgerJson)!.DeliveredHashes);

        Assert.True((await h.Service.WithdrawAsync("wi-1", queued.Id!, h.Run.Id, null)).Ok);

        var ledger = PrCommentLedgerJson.TryParse(h.LedgerJson)!;
        Assert.DoesNotContain(hash, ledger.DeliveredHashes);
        Assert.Contains(PrCommentLedger.KeyFor("review", "11"), ledger.DeliveredIds);
    }

    [Theory]
    [InlineData("queued by another run")]
    [InlineData("queued by a chat session")]
    [InlineData("queued before owners were recorded")]
    [InlineData("asked for by a caller with no identity")]
    public async Task A_write_that_is_not_the_callers_is_refused_and_left_alone(string whose)
    {
        using var h = await Harness.CreateAsync();
        string writeId;
        Guid? callerRun = h.Run.Id;
        switch (whose)
        {
            case "queued by another run":
                writeId = (await h.Service.ReplyAsync("wi-1", "11", "their answer", Guid.NewGuid())).Id!;
                break;
            case "queued by a chat session":
                writeId = (await h.Service.ReplyAsync("wi-1", "11", "their answer", null, Guid.NewGuid())).Id!;
                break;
            case "queued before owners were recorded":
                await PrRunSeed.SetQueueAsync(h.Db, h.Run.Id, LegacyQueue);
                writeId = "legacy000001";
                break;
            default:
                writeId = (await h.Service.ReplyAsync("wi-1", "11", "an answer", h.Run.Id)).Id!;
                callerRun = null;
                break;
        }
        var queueBefore = h.QueueJson;
        var ledgerBefore = h.LedgerJson;
        h.ForgetSetup();

        var refused = await h.Service.WithdrawAsync("wi-1", writeId, callerRun, null);

        Assert.False(refused.Ok);
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));
        if (callerRun is not null)
            Assert.Contains("human", refused.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(queueBefore, h.QueueJson);
        Assert.Equal(ledgerBefore, h.LedgerJson);
        Assert.Empty(h.Notified);
        Assert.Empty(h.Logged);
    }

    [Theory]
    [InlineData("already sent")]
    [InlineData("never existed")]
    [InlineData("only on another work item's run")]
    [InlineData("on a work item with no pull request")]
    public async Task A_write_that_is_not_pending_here_is_refused_with_a_reason(string where)
    {
        using var h = await Harness.CreateAsync();
        var kept = await h.Service.CommentAsync("wi-1", "Something that should still go out.", h.Run.Id);
        string writeId;
        Guid callerRun = h.Run.Id;
        switch (where)
        {
            case "already sent":
                var sent = await h.Service.ReplyAsync("wi-1", "11", "sent already", h.Run.Id);
                // The PR node claims what it sends by taking it out of the queue.
                await PrRunSeed.SetQueueAsync(h.Db, h.Run.Id, PrCommentQueueJson.Serialize(h.Queue.Where(w => w.Id != sent.Id).ToList()));
                writeId = sent.Id!;
                break;
            case "never existed":
                writeId = "000000000000";
                break;
            case "only on another work item's run":
                // The caller's own write, on the queue of the run it belongs to,
                // which is not this work item's current run.
                var elsewhere = await h.AddRunAsync("wi-2");
                callerRun = elsewhere.Id;
                writeId = (await h.Service.CommentAsync("wi-2", "about the other item", elsewhere.Id)).Id!;
                break;
            default:
                writeId = kept.Id!;
                await PrRunSeed.SetPrUrlAsync(h.Db, h.Run.Id, null);
                break;
        }
        var before = h.Columns();
        h.ForgetSetup();

        var refused = await h.Service.WithdrawAsync("wi-1", writeId, callerRun, null);

        Assert.False(refused.Ok);
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));
        Assert.Equal(before, h.Columns());
        Assert.Empty(h.Notified);
        Assert.Empty(h.Logged);
    }

    [Fact]
    public async Task Withdrawing_never_reaches_the_forge()
    {
        using var h = await Harness.CreateAsync();
        var queued = await h.Service.CommentAsync("wi-1", "a comment to take back", h.Run.Id);
        h.ForgetSetup();

        Assert.True((await h.Service.WithdrawAsync("wi-1", queued.Id!, h.Run.Id, null)).Ok);

        Assert.Empty(h.Remote.Invocations);
    }

    // -- Listing --------------------------------------------------------------

    [Fact]
    public async Task A_caller_sees_its_own_pending_writes_and_nobody_elses()
    {
        using var h = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        await PrRunSeed.SetQueueAsync(h.Db, h.Run.Id, LegacyQueue);
        var reply = await h.Service.ReplyAsync("wi-1", "11", "my answer", h.Run.Id);
        var comment = await h.Service.CommentAsync("wi-1", "my note on the round", h.Run.Id);
        var resolve = await h.Service.ResolveAsync("wi-1", "PRRT_12", h.Run.Id);
        await h.Service.ReplyAsync("wi-1", "12", "another run's answer", Guid.NewGuid());
        var chats = await h.Service.ReplyAsync("wi-1", "21", "the chat's answer", null, chat);
        h.ForgetSetup();

        var mine = await h.Service.ListQueuedAsync("wi-1", h.Run.Id, null);
        var theirs = await h.Service.ListQueuedAsync("wi-1", null, chat);

        Assert.Equal(
            new[] { reply.Id, comment.Id, resolve.Id }.Order(),
            mine.Writes.Select(w => w.Id).Order());
        var answer = mine.Writes.Single(w => w.Id == reply.Id);
        Assert.Equal(PrQueuedWrite.Reply, answer.Kind);
        Assert.Equal("11", answer.TargetId);
        Assert.Equal("11", answer.ItemId);
        Assert.Equal("my answer", answer.Body);
        Assert.Equal("src/A.cs", answer.Path);
        Assert.Equal(10, answer.Line);
        Assert.Null(mine.Writes.Single(w => w.Id == comment.Id).ItemId);
        Assert.Equal("PRRT_12", mine.Writes.Single(w => w.Id == resolve.Id).TargetId);

        var chatWrite = Assert.Single(theirs.Writes);
        Assert.Equal(chats.Id, chatWrite.Id);
        Assert.Equal("21", chatWrite.ItemId);

        Assert.Empty(h.Remote.Invocations);
    }

    [Fact]
    public async Task Listing_on_a_work_item_with_no_pull_request_is_empty_and_says_why()
    {
        using var h = await Harness.CreateAsync();
        await h.Service.CommentAsync("wi-1", "queued before the pull request went away", h.Run.Id);
        await PrRunSeed.SetPrUrlAsync(h.Db, h.Run.Id, null);

        var listed = await h.Service.ListQueuedAsync("wi-1", h.Run.Id, null);

        Assert.Empty(listed.Writes);
        Assert.False(string.IsNullOrWhiteSpace(listed.Message));
    }

    [Fact]
    public async Task Listing_reads_only_the_work_items_current_run()
    {
        using var h = await Harness.CreateAsync();
        var elsewhere = await h.AddRunAsync("wi-2");
        await h.Service.CommentAsync("wi-2", "about the other item", elsewhere.Id);

        var listed = await h.Service.ListQueuedAsync("wi-1", elsewhere.Id, null);

        Assert.Empty(listed.Writes);
    }
}
