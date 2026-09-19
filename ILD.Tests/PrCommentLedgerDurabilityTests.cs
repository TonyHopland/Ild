using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The ledger is what stops the loop answering itself, and every writer on the
/// PR path writes the whole run row — the engine's park write one step after the
/// PR node records a comment id, and the heartbeat's snapshot write on every
/// tick. A mocked store cannot show a full-row write clobbering a column, so
/// these run against the real (SQLite) store.
/// </summary>
public class PrCommentLedgerDurabilityTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string CloneUrl = "https://github.com/team/repo.git";
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";

    private sealed record Seeded(LoopRun Run, LoopRunNode RunNode, Guid PrNodeId);

    private static async Task<Seeded> SeedParkedRunAsync(TestDb db, params string[] wiredEdges)
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
        var next = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.AI, Label = "fix", Config = "{}" };
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

        return new Seeded(run, runNode, prNode.Id);
    }

    private static RemotePrSnapshot Snapshot()
        => new("t", "b", "open", false, null, null, RemotePrCiStatus.None,
            Array.Empty<RemotePrCheck>(), false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow);

    private static RemotePrReviewItem Inline(string id, string body = "this allocation is wrong")
        => new("review", id, $"t{id}", "r1", "src/A.cs", 10, body, "Copilot", Head,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewItem Posted(string id, string body)
        => new("issue", id, null, null, null, null, body, "ild-service-account", Head,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Fetched(params RemotePrReviewItem[] items)
        => new(new[] { new RemotePrReviewSummary("r1", "COMMENTED", "body", Head, DateTime.UtcNow, "Copilot", false) },
            items, Head, null);

    private static async Task<LoopRun> RereadAsync(TestDb db, Guid runId)
    {
        var run = await new LoopRunStore(db.Fresh()).GetByIdAsync(runId);
        Assert.NotNull(run);
        return run!;
    }

    [Fact]
    public async Task The_comment_id_the_pr_node_recorded_survives_the_park_write_that_follows_it()
    {
        // The engine parks the run one step after the node posts, and that park
        // writes the whole row: a ledger the node only wrote through a targeted
        // update would be reverted to what the in-memory instance still holds.
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db);
        var run = seeded.Run;
        run.HumanFeedbackReason = null;
        run.Status = LoopRunStatus.Running;
        await db.LoopRuns.UpdateRunAsync(run);

        var repoId = Guid.NewGuid();
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync("wi-1"))
            .ReturnsAsync(new WorkItemView { Id = "wi-1", Title = "T", Description = "D", RepositoryId = repoId });
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(new Repository
        {
            Id = repoId, Name = "repo", CloneUrl = CloneUrl, DefaultBranch = "main", RemoteProviderId = Guid.NewGuid(),
        });
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "7", It.IsAny<string>()))
            .ReturnsAsync(new RemotePrWriteResult(true, "4053396920", null));

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        services.AddSingleton(db.LoopRuns);
        var sp = services.BuildServiceProvider();

        var node = new LoopNode
        {
            Id = seeded.PrNodeId,
            NodeType = NodeType.PR,
            Config = """{"prCommentTemplate":"Answered every point."}""",
        };

        await foreach (var _ in new PRNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
        {
        }

        // …and now the engine parks the run, writing the whole row.
        run.Status = LoopRunStatus.WaitingHuman;
        run.HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge;
        run.PrPolledEdgeStates = null;
        await db.LoopRuns.UpdateRunAsync(run);

        var reread = await RereadAsync(db, run.Id);
        Assert.NotNull(reread.PrCommentLedger);

        var ourOwnComment = Fetched(Posted("4053396920", "Answered every point."));
        Assert.Empty(PrCommentDelivery.Decide(ourOwnComment, Head, PrCommentLedgerJson.TryParse(reread.PrCommentLedger)).Items);
    }

    [Fact]
    public async Task A_delivery_recorded_this_tick_survives_the_heartbeats_own_snapshot_write()
    {
        // The poll pass writes the snapshot on every tick from the run instance
        // it is holding. Written after the ledger, it reverts the delivery — and
        // the same items fire again next tick, for ever, with no comment of
        // ILD's own anywhere in it for the marker to catch.
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrNodeEdges.OnComment);
        var watching = PrCommentLedgerJson.Serialize(
            PrCommentDelivery.Decide(Fetched(), Head, null).Ledger);
        await db.LoopRuns.SetPrCommentLedgerAsync(seeded.Run.Id, watching);

        var fetched = Fetched(Inline("4049159495"));
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Snapshot());
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(fetched);
        var engine = new Mock<ILoopEngine>();

        var service = new PrStatusPollService(
            new LoopRunStore(db.Fresh()), remote.Object, engine.Object, Mock.Of<IRunNotifier>(),
            NullLogger<PrStatusPollService>.Instance);
        await service.PollOnceAsync();

        engine.Verify(e => e.SignalNodeResultAsync(seeded.Run.Id, seeded.RunNode.Id,
            It.Is<NodeSignal>(s => s.EdgeName == PrNodeEdges.OnComment)), Times.Once);

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.NotNull(reread.PrSnapshot);
        Assert.Empty(PrCommentDelivery.Decide(fetched, Head, PrCommentLedgerJson.TryParse(reread.PrCommentLedger)).Items);
    }

    [Fact]
    public async Task A_quiet_tick_leaves_the_ledger_where_it_was()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db, PrNodeEdges.OnComment);
        var alreadySeen = Fetched(Inline("4049159495"));
        var watching = PrCommentLedgerJson.Serialize(PrCommentDelivery.Decide(alreadySeen, Head, null).Ledger);
        await db.LoopRuns.SetPrCommentLedgerAsync(seeded.Run.Id, watching);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7")).ReturnsAsync(Snapshot());
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(alreadySeen);
        var engine = new Mock<ILoopEngine>();

        await new PrStatusPollService(
            new LoopRunStore(db.Fresh()), remote.Object, engine.Object, Mock.Of<IRunNotifier>(),
            NullLogger<PrStatusPollService>.Instance).PollOnceAsync();

        engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.NotNull(reread.PrCommentLedger);
        Assert.Empty(PrCommentDelivery.Decide(alreadySeen, Head, PrCommentLedgerJson.TryParse(reread.PrCommentLedger)).Items);
    }

    [Fact]
    public async Task Setting_the_ledger_touches_no_other_column_of_the_run()
    {
        using var db = new TestDb();
        var seeded = await SeedParkedRunAsync(db);
        seeded.Run.PrSnapshot = PrSnapshotJson.Serialize(Snapshot());
        await db.LoopRuns.UpdateRunAsync(seeded.Run);

        await db.LoopRuns.SetPrCommentLedgerAsync(seeded.Run.Id, "{\"v\":1}");

        var reread = await RereadAsync(db, seeded.Run.Id);
        Assert.Equal("{\"v\":1}", reread.PrCommentLedger);
        Assert.NotNull(reread.PrSnapshot);
        Assert.Equal(PrUrl, reread.PrUrl);
    }
}
