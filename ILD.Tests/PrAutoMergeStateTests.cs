using ILD.Data;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The AutoMerge fallback's one attempt per pull request rests on its state
/// surviving everything else that writes the run row — the heartbeat's own
/// snapshot write on every tick, the engine's park — and a restart. A mocked
/// store cannot show a full-row write clobbering a column, so these run against
/// the real (SQLite) store.
/// </summary>
public class PrAutoMergeStateTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";

    private sealed record Seeded(LoopRun Run, LoopRunNode RunNode);

    private static async Task<Seeded> SeedParkedRunAsync(TestDb db, PrAutoMerge? state, params string[] wiredEdges)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var prNode = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr", Config = "{}" };
        var next = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.AI, Label = "next", Config = "{}" };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopNodes.AddRange(prNode, next);
        foreach (var name in wiredEdges)
            db.Context.LoopNodeEdges.Add(new LoopNodeEdge
            {
                Id = Guid.NewGuid(),
                SourceNodeId = prNode.Id,
                TargetNodeId = next.Id,
                EdgeType = EdgeType.Custom,
                Name = name,
            });
        await db.Context.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.WaitingHuman,
            HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            PrUrl = PrUrl,
            CurrentNodeId = prNode.Id,
            StartedAt = DateTime.UtcNow,
            PrAutoMerge = state,
        };
        await db.LoopRuns.CreateRunAsync(run);

        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = prNode.Id,
            Status = LoopRunNodeStatus.WaitingHuman,
            CreatedAt = DateTime.UtcNow,
        };
        await db.LoopRuns.CreateRunNodeAsync(runNode);
        return new Seeded(run, runNode);
    }

    private static RemotePrSnapshot Green()
        => new RemotePrSnapshot("t", "b", "open", false, true, "clean", RemotePrCiStatus.Passed,
            Array.Empty<RemotePrCheck>(), false, false,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow) with { Draft = false, MergeBlocked = false, HeadSha = "head-1" };

    private static async Task<LoopRun> RereadAsync(TestDb db, Guid runId)
    {
        var run = await new LoopRunStore(db.Fresh()).GetByIdAsync(runId);
        Assert.NotNull(run);
        return run!;
    }

    [Fact]
    public async Task The_state_moves_only_from_what_the_writer_expected()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, state: null);
        var store = new LoopRunStore(db.Fresh());
        var id = seeded.Run.Id;

        Assert.True(await store.TrySetPrAutoMergeAsync(id, null, PrAutoMerge.IldPending));
        // A second PR-node write finds the state already set.
        Assert.False(await store.TrySetPrAutoMergeAsync(id, null, PrAutoMerge.Native));
        Assert.True(await store.TrySetPrAutoMergeAsync(id, PrAutoMerge.IldPending, PrAutoMerge.IldMerging));
        // A second claim on the same pull request loses.
        Assert.False(await store.TrySetPrAutoMergeAsync(id, PrAutoMerge.IldPending, PrAutoMerge.IldMerging));
        Assert.True(await store.TrySetPrAutoMergeAsync(id, PrAutoMerge.IldMerging, PrAutoMerge.IldFailed, "Required review missing"));

        var reread = await RereadAsync(db, id);
        Assert.Equal(PrAutoMerge.IldFailed, reread.PrAutoMerge);
        Assert.Equal("Required review missing", reread.PrAutoMergeError);
        Assert.Equal(PrUrl, reread.PrUrl);
    }

    [Theory]
    [InlineData(LoopRunStatus.Running, null)]
    [InlineData(LoopRunStatus.Cancelled, HumanFeedbackReasons.PrAwaitingMerge)]
    [InlineData(LoopRunStatus.WaitingHuman, HumanFeedbackReasons.NodeFailed)]
    public async Task The_merge_attempt_is_not_claimed_on_a_run_that_has_stopped_waiting_on_the_merge(
        LoopRunStatus status, string? reason)
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrAutoMerge.IldPending);
        // Moved on, stopped or re-parked elsewhere after the heartbeat loaded it.
        await db.Context.LoopRuns.Where(r => r.Id == seeded.Run.Id).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, status)
            .SetProperty(r => r.HumanFeedbackReason, reason), TestContext.Current.CancellationToken);

        Assert.False(await new LoopRunStore(db.Fresh())
            .TrySetPrAutoMergeAsync(seeded.Run.Id, PrAutoMerge.IldPending, PrAutoMerge.IldMerging));
        Assert.Equal(PrAutoMerge.IldPending, (await RereadAsync(db, seeded.Run.Id)).PrAutoMerge);
    }

    [Fact]
    public async Task A_full_row_write_from_an_instance_loaded_earlier_cannot_put_the_attempt_back()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrAutoMerge.IldPending);
        var stale = await db.LoopRuns.GetByIdAsync(seeded.Run.Id);

        Assert.True(await new LoopRunStore(db.Fresh())
            .TrySetPrAutoMergeAsync(seeded.Run.Id, PrAutoMerge.IldPending, PrAutoMerge.IldFailed, "refused"));

        stale!.PrSnapshot = PrSnapshotJson.Serialize(Green());
        stale.PrAutoMerge = PrAutoMerge.IldPending;
        stale.PrAutoMergeError = null;
        await db.LoopRuns.UpdateRunAsync(stale);

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.Equal(PrAutoMerge.IldFailed, reread.PrAutoMerge);
        Assert.Equal("refused", reread.PrAutoMergeError);
        Assert.NotNull(reread.PrSnapshot);
    }

    [Fact]
    public async Task Recording_a_push_clears_the_snapshot_and_sets_the_pushed_commit()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, null);
        var snapshot = new LoopRunStore(db.Fresh());
        var run = await snapshot.GetByIdAsync(seeded.Run.Id);
        run!.PrSnapshot = PrSnapshotJson.Serialize(Green());
        await snapshot.UpdateRunAsync(run);

        await new LoopRunStore(db.Fresh()).RecordPrPushAsync(seeded.Run.Id, "pushed");

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.Null(reread.PrSnapshot);
        Assert.Equal("pushed", reread.PrPushedCommit);
    }

    [Fact]
    public async Task A_full_row_write_from_an_instance_loaded_before_the_push_cannot_drop_the_pushed_commit()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, null);
        var stale = await db.LoopRuns.GetByIdAsync(seeded.Run.Id);

        await new LoopRunStore(db.Fresh()).RecordPrPushAsync(seeded.Run.Id, "pushed");

        stale!.PrPushedCommit = null;
        stale.UpdatedAt = DateTime.UtcNow;
        await db.LoopRuns.UpdateRunAsync(stale);

        Assert.Equal("pushed", (await RereadAsync(db, seeded.Run.Id)).PrPushedCommit);
    }

    [Fact]
    public async Task Clearing_the_pushed_commit_only_clears_the_one_it_was_given()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, null);
        await new LoopRunStore(db.Fresh()).RecordPrPushAsync(seeded.Run.Id, "second");

        await new LoopRunStore(db.Fresh()).ClearPrPushedCommitAsync(seeded.Run.Id, "first");
        Assert.Equal("second", (await RereadAsync(db, seeded.Run.Id)).PrPushedCommit);

        await new LoopRunStore(db.Fresh()).ClearPrPushedCommitAsync(seeded.Run.Id, "second");
        Assert.Null((await RereadAsync(db, seeded.Run.Id)).PrPushedCommit);
    }

    // ---- A person answers the parked node while the heartbeat is working on the run ----

    private static async Task MoveOnAsync(TestDb db, Guid runId)
    {
        // What the engine leaves behind once the PR node has been answered and a Human node asks.
        var versionId = (await RereadAsync(db, runId)).LoopTemplateVersionId;
        var human = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = versionId, NodeType = NodeType.Human, Label = "ask", Config = "{}" };
        await using var other = db.Fresh();
        other.LoopNodes.Add(human);
        await other.SaveChangesAsync();
        await other.LoopRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.CurrentNodeId, human.Id)
            .SetProperty(r => r.Status, LoopRunStatus.WaitingHuman)
            .SetProperty(r => r.HumanFeedbackReason, "Question"));
    }

    private sealed record Pass(Mock<IRemoteProvider> Remote, Mock<ILoopEngine> Engine, Mock<IRunNotifier> Notifier,
        Mock<IWorkItemNotifier> Board, Func<Task> Poll);

    private static Pass NewPass(TestDb db)
    {
        var remote = new Mock<IRemoteProvider>();
        var engine = new Mock<ILoopEngine>();
        var notifier = new Mock<IRunNotifier>();
        var board = new Mock<IWorkItemNotifier>();
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync("wi-1"))
            .ReturnsAsync(new WorkItemView { Id = "wi-1", Title = "T", Tags = new[] { "AutoMerge" } });
        return new Pass(remote, engine, notifier, board, () => new PrStatusPollService(
            new LoopRunStore(db.Fresh()), remote.Object, engine.Object, notifier.Object,
            NullLogger<PrStatusPollService>.Instance, workItems.Object, Mock.Of<IEventLogService>(), board.Object)
            .PollOnceAsync(TestContext.Current.CancellationToken));
    }

    private static void AssertMovedOnAndUntouched(LoopRun reread, Guid originalNode, Pass pass)
    {
        Assert.NotEqual(originalNode, reread.CurrentNodeId);
        Assert.Equal(LoopRunStatus.WaitingHuman, reread.Status);
        Assert.Equal("Question", reread.HumanFeedbackReason);
        pass.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        pass.Remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        pass.Notifier.Verify(n => n.PrSnapshotChangedAsync(It.IsAny<Guid>()), Times.Never);
        pass.Board.Verify(n => n.PrStatusChangedAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_run_answered_during_the_forge_fetch_keeps_where_the_engine_put_it_and_the_pass_stops()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrAutoMerge.IldPending, LoopOutputs.OnCiPassed);
        var pass = NewPass(db);
        pass.Remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).Returns(async () =>
        {
            await MoveOnAsync(db, seeded.Run.Id);
            return Green();
        });

        await pass.Poll();

        var reread = await RereadAsync(db, seeded.Run.Id);
        AssertMovedOnAndUntouched(reread, seeded.Run.CurrentNodeId!.Value, pass);
        Assert.Null(reread.PrSnapshot);
        Assert.Null(reread.PrPolledEdgeStates);
        Assert.Equal(PrAutoMerge.IldPending, reread.PrAutoMerge);
    }

    [Fact]
    public async Task A_run_answered_while_the_merge_is_refused_for_a_moved_head_is_not_written_back()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrAutoMerge.IldPending, LoopOutputs.OnCiPassed);
        var pass = NewPass(db);
        pass.Remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Green());
        pass.Remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7", "head-1")).Returns(async () =>
        {
            await MoveOnAsync(db, seeded.Run.Id);
            throw new RemoteHeadMovedException("head-1", "head-2");
        });

        await pass.Poll();

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.NotEqual(seeded.Run.CurrentNodeId, reread.CurrentNodeId);
        Assert.Equal(LoopRunStatus.WaitingHuman, reread.Status);
        Assert.Equal("Question", reread.HumanFeedbackReason);
        pass.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        // The first write landed while the run was still parked; the clear after the refusal did not.
        Assert.NotNull(reread.PrSnapshot);
        pass.Notifier.Verify(n => n.PrSnapshotChangedAsync(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task A_tick_with_nothing_moving_the_run_stores_the_result_and_leaves_its_position_alone()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, null);
        var pass = NewPass(db);
        pass.Remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Green());

        await pass.Poll();

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.NotNull(reread.PrSnapshot);
        Assert.Equal(seeded.Run.CurrentNodeId, reread.CurrentNodeId);
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, reread.HumanFeedbackReason);
        pass.Notifier.Verify(n => n.PrSnapshotChangedAsync(seeded.Run.Id), Times.Once);
    }

    [Fact]
    public async Task A_merge_webhook_handled_with_a_run_loaded_before_it_moved_on_does_not_revert_it()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, null);
        var events = new Mock<IEventLogService>();
        // The comment is recorded after the run was loaded and before the merge is booked.
        events.Setup(e => e.AppendAsync(seeded.Run.Id, It.IsAny<EventType>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .Returns(async () =>
            {
                await MoveOnAsync(db, seeded.Run.Id);
                return 1L;
            });
        var service = new PrSyncService(
            new LoopRunStore(db.Fresh()), events.Object, Mock.Of<IWorkItemManager>(), Mock.Of<ILoopEngine>(),
            Mock.Of<IPrStatusPoller>(), TestActors.Instance);

        await service.HandleWebhookAsync(new WebhookPayload("pull_request.merged", "repo-1", "7", PrUrl, "looks good", "merged"));

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.True(reread.IsPrMerged);
        Assert.NotEqual(seeded.Run.CurrentNodeId, reread.CurrentNodeId);
        Assert.Equal("Question", reread.HumanFeedbackReason);
    }

    [Fact]
    public async Task A_merge_refused_because_the_head_moved_is_put_back_to_pending_not_failed()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrAutoMerge.IldPending);
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Green());
        remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7", "head-1"))
            .ThrowsAsync(new RemoteHeadMovedException("head-1", "head-2"));
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync("wi-1"))
            .ReturnsAsync(new WorkItemView { Id = "wi-1", Title = "T", Tags = new[] { "AutoMerge" } });

        await new PrStatusPollService(
            new LoopRunStore(db.Fresh()), remote.Object, Mock.Of<ILoopEngine>(), Mock.Of<IRunNotifier>(),
            NullLogger<PrStatusPollService>.Instance, workItems.Object, Mock.Of<IEventLogService>())
            .PollOnceAsync(TestContext.Current.CancellationToken);

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.Equal(PrAutoMerge.IldPending, reread.PrAutoMerge);
        Assert.Null(reread.PrAutoMergeError);
    }

    [Fact]
    public async Task A_refused_merge_stays_parked_is_not_retried_after_a_restart_and_a_human_merge_moves_on()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrAutoMerge.IldPending, LoopOutputs.OnMerged);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Green());
        remote.Setup(r => r.MergePullRequestAsync(It.IsAny<string>(), "7", It.IsAny<string?>())).ReturnsAsync(false);
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync("wi-1"))
            .ReturnsAsync(new WorkItemView { Id = "wi-1", Title = "T", Tags = new[] { "AutoMerge" } });
        var engine = new Mock<ILoopEngine>();

        // Each pass on its own store and service, as each heartbeat scope — and a restart — gets.
        PrStatusPollService Service() => new(
            new LoopRunStore(db.Fresh()), remote.Object, engine.Object, Mock.Of<IRunNotifier>(),
            NullLogger<PrStatusPollService>.Instance, workItems.Object, Mock.Of<IEventLogService>());

        await Service().PollOnceAsync(TestContext.Current.CancellationToken);

        var afterFailure = await RereadAsync(db, seeded.Run.Id);
        Assert.Equal(PrAutoMerge.IldFailed, afterFailure.PrAutoMerge);
        Assert.False(string.IsNullOrWhiteSpace(afterFailure.PrAutoMergeError));
        Assert.Equal(LoopRunStatus.WaitingHuman, afterFailure.Status);
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, afterFailure.HumanFeedbackReason);
        Assert.Equal(seeded.Run.CurrentNodeId, afterFailure.CurrentNodeId);
        Assert.Contains(await new LoopRunStore(db.Fresh()).GetPrAwaitingMergeRunsAsync(), r => r.Id == seeded.Run.Id);

        await Service().PollOnceAsync(TestContext.Current.CancellationToken);
        remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);

        remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(
            Green() with { State = "closed", Merged = true });
        await Service().PollOnceAsync(TestContext.Current.CancellationToken);

        engine.Verify(e => e.SignalNodeResultAsync(seeded.Run.Id, seeded.RunNode.Id,
            It.Is<NodeSignal>(s => s.EdgeName == LoopOutputs.OnMerged)), Times.Once);
        remote.Verify(r => r.MergePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }
}
