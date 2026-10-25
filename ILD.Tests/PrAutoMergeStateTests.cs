using ILD.Data;
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
