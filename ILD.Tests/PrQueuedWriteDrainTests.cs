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

    private static PrQueuedWrite Comment(string id, string targetId, string body)
        => new(id, PrQueuedWrite.Comment, targetId, body, null, null, DateTime.UtcNow);

    private static PrQueuedWrite Resolve(string id, string threadId)
        => new(id, PrQueuedWrite.Resolve, threadId, null, "src/A.cs", 10, DateTime.UtcNow);

    private sealed class Fixture
    {
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRunNotifier> Notifier { get; } = new();
        public List<Guid> QueueChanges { get; } = new();
        public LoopRun Run { get; }
        public List<(string Kind, string Target, string? Body)> Written { get; } = new();
        public string? RecordedLedger { get; private set; }
        public List<string?> QueueWrites { get; } = new();
        public string? PostedComment { get; private set; }

        public Fixture(IReadOnlyList<PrQueuedWrite> queued)
        {
            Row = queued.Count == 0 ? null : PrCommentQueueJson.Serialize(queued);
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                PrUrl = PrUrl,
                CurrentNodeId = Guid.NewGuid(),
                // Deliberately NOT the queue: the engine loaded this instance
                // before the round queued anything, so a node that trusts it
                // sends the wrong list — or, with no comment template, never
                // enters the branch that sends at all.
                PrCommentQueue = null,
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

            Notifier.Setup(n => n.PrQueueChangedAsync(It.IsAny<Guid>()))
                .Callback<Guid>(QueueChanges.Add)
                .Returns(Task.CompletedTask);

            Runs.Setup(s => s.SetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => RecordedLedger = json)
                .Returns(Task.CompletedTask);

            // The ledger is mutated by compare-and-set too: every writer of it
            // reads the column and writes only if it still holds what it read.
            Runs.Setup(s => s.GetPrCommentLedgerAsync(It.IsAny<Guid>())).ReturnsAsync(() => Run.PrCommentLedger);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentLedger, StringComparison.Ordinal)) return false;
                    Run.PrCommentLedger = json;
                    RecordedLedger = json;
                    return true;
                });

            // The row, not the instance the engine carries. The node reads and
            // claims the queue through these, so anything queued or dropped
            // after Run was loaded is visible to it — which is the whole point
            // of the claim.
            Runs.Setup(s => s.GetPrCommentQueueAsync(Run.Id)).ReturnsAsync(() =>
            {
                Reads++;
                var read = Row;
                // The row is allowed to move out from under this read, which is
                // what a human dropping something mid-round does. The claim's
                // compare-and-set then fails against `read` and it tries again.
                AfterRead?.Invoke();
                return read;
            });
            Runs.Setup(s => s.TrySetPrCommentQueueAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Row, StringComparison.Ordinal)) return false;
                    QueueWrites.Add(json);
                    Row = json;
                    return true;
                });
        }

        /// <summary>The persisted column, which only the store members above touch.</summary>
        public string? Row { get; set; }

        public int Reads { get; private set; }

        /// <summary>Runs after each read of the queue and before the claim that follows it.</summary>
        public Action? AfterRead { get; set; }

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
            services.AddSingleton(Notifier.Object);

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
        Assert.Null(f.Row);
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
        Assert.Null(f.Row);
        Assert.Null(f.RecordedLedger);
    }

    [Fact]
    public async Task A_round_that_answered_on_the_threads_says_nothing_general_as_well()
    {
        // The node has a pr_reply template AND the round queued answers. The
        // answers still go out — that is the whole point of draining on this
        // path too — but the template comment does not: "Addressed the latest
        // comments." posted under a set of replies that already are the
        // addressing is a second notification carrying nothing.
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles."), Resolve("w2", "PRRT_t1") });
        // The round was handed two findings and has answered both: nothing is
        // left waiting, which is what makes the general comment redundant.
        f.Run.PrCommentLedger = PrCommentLedgerJson.Serialize(
            PrCommentLedger.Empty with { Head = Head, Unanswered = Array.Empty<string>() });

        var outcomes = await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.Equal(2, f.Written.Count);
        Assert.Null(f.PostedComment);
        Assert.Null(f.Row);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task A_round_that_answered_only_some_of_them_still_says_what_it_did()
    {
        // Four fixed in code and one rebutted on its thread: the general comment
        // is the only place the four are reported, so it has to go out.
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });
        f.Run.PrCommentLedger = PrCommentLedgerJson.Serialize(
            PrCommentLedger.Empty with
            {
                Head = Head,
                Unanswered = new[] { "a-finding-nobody-answered" },
            });

        await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.NotNull(f.PostedComment);
        Assert.Single(f.Written);
    }

    [Fact]
    public async Task A_round_that_only_resolved_threads_still_says_what_it_did()
    {
        // A resolve closes a thread and says nothing to anyone reading the pull
        // request, so it is not an answer and cannot stand in for one.
        var f = new Fixture(new[] { Resolve("w1", "PRRT_t1") });
        f.Run.PrCommentLedger = PrCommentLedgerJson.Serialize(
            PrCommentLedger.Empty with { Head = Head, Unanswered = Array.Empty<string>() });

        await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.NotNull(f.PostedComment);
        Assert.Single(f.Written);
    }

    [Fact]
    public async Task A_round_with_nothing_to_answer_still_posts_its_comment()
    {
        // The other half: with no answers queued, the template comment is the
        // only thing the round has to say, so it must still go out.
        var f = new Fixture(Array.Empty<PrQueuedWrite>());

        var outcomes = await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.NotNull(f.PostedComment);
        Assert.Contains("Answered every point.", f.PostedComment!, StringComparison.Ordinal);
        Assert.Empty(f.Written);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task A_drop_that_lands_while_the_node_is_running_still_stops_the_comment()
    {
        // The human's window runs until the round reaches this node, so a drop
        // landing while it executes has to be honoured. Reading the queue off
        // the instance the engine has carried since the iteration began would
        // post the answer anyway, after a person had explicitly stopped it.
        var f = new Fixture(new[]
        {
            Reply("w1", "4049159495", "This one was stopped."),
            Reply("w2", "4051372317", "And this one stands."),
        });
        f.AfterRead = () =>
        {
            // The drop lands after the claim has read the queue but before it
            // clears it — the narrowest version of the window, and the one a
            // read-then-write drain gets wrong. Only that first read is
            // overtaken; the retry finds a settled row. A comment template is
            // set so this read is the claim's own and not the branch's gate.
            if (f.Reads != 1) return;
            f.Row = PrCommentQueueJson.Serialize(new[] { Reply("w2", "4051372317", "And this one stands.") });
        };

        await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.DoesNotContain(f.Written, w => w.Target == "4049159495");
        var written = Assert.Single(f.Written);
        Assert.Equal("4051372317", written.Target);
        Assert.True(f.Reads >= 2, "the claim kept what it first read instead of re-reading");
        Assert.Null(f.Row);
    }

    [Fact]
    public async Task A_reply_queued_during_the_round_goes_out_though_the_node_has_no_comment_to_post()
    {
        // The instance says nothing is waiting, because the agent queued after
        // it was loaded. A node with no comment template decides whether to run
        // this branch at all on that answer, so trusting it strands every reply
        // an ordinary round produces.
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });
        Assert.Null(f.Run.PrCommentQueue);

        var outcomes = await f.RunNodeAsync(commentTemplate: null);

        Assert.Single(f.Written);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task Claiming_the_queue_says_it_changed_so_the_panel_stops_offering_to_stop_it()
    {
        // Once the claim lands the answers are on their way out. A panel still
        // showing them offers a Drop that cannot work.
        var f = new Fixture(new[] { Reply("w1", "4049159495", "That compiles.") });

        await f.RunNodeAsync();

        Assert.Equal(f.Run.Id, Assert.Single(f.QueueChanges));
    }

    [Fact]
    public async Task A_round_with_nothing_queued_announces_no_queue_change()
    {
        var f = new Fixture(Array.Empty<PrQueuedWrite>());

        await f.RunNodeAsync(commentTemplate: "Answered every point.");

        Assert.Empty(f.QueueChanges);
    }

    [Fact]
    public async Task A_refused_answer_puts_its_finding_back_within_reach()
    {
        // The heartbeat marked the finding delivered when it handed it over. If
        // the forge refuses the answer, leaving it marked retires the finding:
        // the thread stays open and no later review ever raises it again.
        const string body = "this allocation is wrong";
        var hash = PrCommentLedger.Fingerprint("src/A.cs", 10, body);
        var f = new Fixture(new[]
        {
            Reply("w1", "4049159495", "That compiles.") with { SourceHash = hash },
        });
        f.Run.PrCommentLedger = PrCommentLedgerJson.Serialize(PrCommentLedger.Empty with
        {
            Head = Head,
            DeliveredIds = new[] { PrCommentLedger.KeyFor("review", "4049159495") },
            DeliveredHashes = new[] { hash },
            WatchedFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        });
        f.Remote.Setup(r => r.ReplyToReviewThreadAsync(CloneUrl, "42", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrWriteResult(false, null, "403 from the forge"));

        await f.RunNodeAsync();

        var after = PrCommentLedgerJson.TryParse(f.RecordedLedger);
        Assert.NotNull(after);
        Assert.DoesNotContain(hash, after!.DeliveredHashes);
        // …but the comment already handed over is still remembered, or the same
        // one fires again next tick and the refusal repeats for ever.
        Assert.Contains(PrCommentLedger.KeyFor("review", "4049159495"), after.DeliveredIds);
    }

    [Fact]
    public async Task An_answer_that_went_out_leaves_its_finding_answered()
    {
        var hash = PrCommentLedger.Fingerprint("src/A.cs", 10, "this allocation is wrong");
        var f = new Fixture(new[]
        {
            Reply("w1", "4049159495", "That compiles.") with { SourceHash = hash },
        });
        f.Run.PrCommentLedger = PrCommentLedgerJson.Serialize(PrCommentLedger.Empty with
        {
            Head = Head,
            DeliveredHashes = new[] { hash },
            WatchedFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        });

        await f.RunNodeAsync();

        Assert.Contains(hash, PrCommentLedgerJson.TryParse(f.RecordedLedger)!.DeliveredHashes);
    }

    [Fact]
    public async Task An_answer_to_something_with_no_thread_goes_out_as_its_own_comment()
    {
        // A top-level comment and a review body have no thread on any forge, so
        // the answer is a pull-request comment of its own — stamped like every
        // other thing ILD posts, or the next heartbeat hands it back to the loop.
        var f = new Fixture(new[] { Comment("w1", "127", "In reply to the comment (127):\n\n> Rename it\n\nRenamed.") });

        await f.RunNodeAsync();

        Assert.NotNull(f.PostedComment);
        Assert.Contains("> Rename it", f.PostedComment!, StringComparison.Ordinal);
        Assert.True(PrCommentMarker.IsStamped(f.PostedComment), "the answer carries no marker");
        Assert.Empty(f.Written);
        Assert.Null(f.Row);
    }

    [Fact]
    public async Task The_id_a_standalone_answer_gets_is_recorded_in_the_space_the_ledger_keys_on()
    {
        // It lands as a pull-request comment, which the ledger keys `issue:` —
        // recording it as `review:` would eventually mark a real review comment
        // as ILD's own and swallow it.
        var f = new Fixture(new[] { Comment("w1", "127", "Renamed.") });

        await f.RunNodeAsync();

        var ledger = PrCommentLedgerJson.TryParse(f.RecordedLedger);
        Assert.NotNull(ledger);
        Assert.Contains(PrCommentLedger.KeyFor("issue", "5000000001"), ledger!.PostedIds);
    }

    [Fact]
    public async Task What_a_standalone_answer_posted_never_fires_the_comment_edge_back_at_it()
    {
        var f = new Fixture(new[] { Comment("w1", "127", "Renamed.") });

        await f.RunNodeAsync();

        var ourOwn = new RemotePrReviewLedger(
            Array.Empty<RemotePrReviewSummary>(),
            new[]
            {
                new RemotePrReviewItem("issue", "5000000001", null, null, null, null,
                    f.PostedComment!, "ild-service-account", Head, DateTime.UtcNow, false, false),
            },
            Head, null);

        Assert.Empty(PrCommentDelivery.Decide(
            ourOwn, Head, PrCommentLedgerJson.TryParse(f.RecordedLedger)).Items);
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
