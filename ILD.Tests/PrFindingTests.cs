using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A reviewer's finding: queued against a file and line like any other write,
/// never posted at call time, sent by the PR node as a line-anchored review
/// comment, and never news to the run that posted it. A re-review that raises
/// the same point again queues nothing, and a reviewer cannot bury a pull
/// request in findings or nits past the configured caps.
/// </summary>
public class PrFindingTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string CloneUrl = "https://github.com/team/repo.git";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Dictionary<string, string> Settings { get; } = new();
        public List<RemotePrReviewItem> OnThePr { get; } = new();

        public Harness()
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(), WorkItemId = "wi-1", PrUrl = PrUrl, Status = LoopRunStatus.Running,
                CurrentNodeId = Guid.NewGuid(),
            };
            Runs.Setup(s => s.GetActiveByWorkItemAsync("wi-1")).ReturnsAsync(() => Run);
            Runs.Setup(s => s.GetLatestByWorkItemAsync("wi-1")).ReturnsAsync(() => Run);
            Runs.Setup(s => s.GetByIdAsync(Run.Id)).ReturnsAsync(() => Run);
            Runs.Setup(s => s.GetPrCommentQueueAsync(Run.Id)).ReturnsAsync(() => Run.PrCommentQueue);
            Runs.Setup(s => s.TrySetPrCommentQueueAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentQueue, StringComparison.Ordinal)) return false;
                    Run.PrCommentQueue = json;
                    return true;
                });
            Runs.Setup(s => s.GetPrCommentLedgerAsync(Run.Id)).ReturnsAsync(() => Run.PrCommentLedger);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentLedger, StringComparison.Ordinal)) return false;
                    Run.PrCommentLedger = json;
                    return true;
                });
            Runs.Setup(s => s.SetPrCommentLedgerAsync(Run.Id, It.IsAny<string?>()))
                .Callback<Guid, string?>((_, json) => Run.PrCommentLedger = json)
                .Returns(Task.CompletedTask);

            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7"))
                .ReturnsAsync(() => new RemotePrReviewLedger(
                    Array.Empty<RemotePrReviewSummary>(), OnThePr.ToArray(), Head, null));
            Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);
        }

        /// <summary>The service as DI builds it, with settings read through the real settings service.</summary>
        public PrReviewService Build()
        {
            var store = new Mock<IAppSettingStore>();
            store.Setup(s => s.GetByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                    Settings.TryGetValue(key, out var value) ? new AppSetting { Key = key, Value = value } : null);

            var services = new ServiceCollection();
            services.AddSingleton(Runs.Object);
            services.AddSingleton(Remote.Object);
            services.AddSingleton(Mock.Of<IRunNotifier>());
            services.AddSingleton(Mock.Of<IEventLogService>());
            services.AddSingleton(store.Object);
            services.AddSingleton<ISchedulerSettingsService, SchedulerSettingsService>();
            return ActivatorUtilities.CreateInstance<PrReviewService>(services.BuildServiceProvider());
        }

        public IReadOnlyList<PrQueuedWrite> Queue => PrCommentQueueJson.TryParse(Run.PrCommentQueue);

        public static RemotePrReviewItem IldFinding(string commentId, string findingId, string severity, bool resolved = false)
            => new("review", commentId, $"PRRT_{commentId}", "r1", "src/Other.cs", 3,
                PrCommentMarker.Stamp("an earlier finding", Guid.NewGuid(), findingId, severity),
                "ild-service-account", Head, DateTime.UtcNow.AddHours(-1), resolved, true,
                FindingId: findingId, FindingSeverity: severity);

        public void VerifyNothingWritten()
        {
            Remote.Verify(r => r.CreateReviewCommentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
            Remote.Verify(r => r.CreatePullRequestCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Remote.Verify(r => r.ReplyToReviewThreadAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }

    // -- Queuing ---------------------------------------------------------------

    [Fact]
    public async Task A_finding_is_queued_not_posted_and_can_be_listed_withdrawn_and_dropped()
    {
        var h = new Harness();
        var service = h.Build();

        var first = await service.FindingAsync("wi-1", "src/A.cs", 12, "important", "This leaks the file handle.", h.Run.Id);
        var second = await service.FindingAsync("wi-1", "src/B.cs", 4, "nit", "Name this for what it holds.", h.Run.Id);

        Assert.True(first.Ok, first.Message);
        Assert.True(second.Ok, second.Message);
        h.VerifyNothingWritten();

        Assert.Equal(2, h.Queue.Count);
        var queued = h.Queue.Single(q => q.Id == first.Id);
        Assert.Equal("finding", queued.Kind);
        Assert.Equal("src/A.cs", queued.Path);
        Assert.Equal(12, queued.Line);
        Assert.Equal("important", queued.Severity);
        Assert.Contains("This leaks the file handle.", queued.Body);
        Assert.False(string.IsNullOrWhiteSpace(queued.FindingId));
        Assert.Equal("nit", h.Queue.Single(q => q.Id == second.Id).Severity);

        var listed = await service.ListQueuedAsync("wi-1", h.Run.Id, null);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), listed.Writes.Select(w => w.Id).Order());

        var withdrawn = await service.WithdrawAsync("wi-1", first.Id!, h.Run.Id, null);
        Assert.True(withdrawn.Ok, withdrawn.Message);
        Assert.True(await ((IPrWriteQueue)service).DropQueuedAsync(h.Run.Id, second.Id!));
        Assert.Empty(h.Queue);
    }

    [Theory]
    [InlineData("src/A.cs", 40, "nit", "  this LEAKS the\n\tfile   handle.  ", true)]
    [InlineData("src/A.cs", 12, "important", "This leaks the file handle.", true)]
    [InlineData("src/B.cs", 12, "important", "This leaks the file handle.", false)]
    [InlineData("src/A.cs", 12, "important", "This leaks the socket.", false)]
    public async Task The_same_finding_restated_is_already_queued_and_a_different_one_is_not(
        string path, int line, string severity, string body, bool same)
    {
        var h = new Harness();
        var service = h.Build();
        var first = await service.FindingAsync("wi-1", "src/A.cs", 12, "important", "This leaks the file handle.", h.Run.Id);
        Assert.True(first.Ok, first.Message);

        var again = await service.FindingAsync("wi-1", path, line, severity, body, h.Run.Id);

        Assert.True(again.Ok, again.Message);
        if (same)
        {
            Assert.Contains("already", again.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(first.Id, Assert.Single(h.Queue).Id);
        }
        else
        {
            Assert.Equal(2, h.Queue.Count);
            Assert.NotEqual(h.Queue[0].FindingId, h.Queue[1].FindingId);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_finding_already_open_on_the_pr_queues_nothing_and_a_resolved_one_is_raised_again(
        bool resolvedOnThePr, bool alreadyOpen)
    {
        var h = new Harness();
        var service = h.Build();

        // A first review queued the finding, and it went out.
        var first = await service.FindingAsync("wi-1", "src/A.cs", 12, "important", "This leaks the file handle.", h.Run.Id);
        var findingId = Assert.Single(h.Queue).FindingId!;
        h.Run.PrCommentQueue = null;
        h.OnThePr.Add(Harness.IldFinding("5001", findingId, "important", resolvedOnThePr) with
        {
            Path = "src/A.cs",
            Line = 12,
        });

        // A re-review raises it again, after the code moved under it.
        var again = await service.FindingAsync("wi-1", "src/A.cs", 15, "important", "This leaks the file handle.", h.Run.Id);

        Assert.True(first.Ok, first.Message);
        Assert.True(again.Ok, again.Message);
        if (alreadyOpen)
        {
            Assert.Contains("already", again.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(h.Queue);
        }
        else
        {
            Assert.Equal(findingId, Assert.Single(h.Queue).FindingId);
        }
    }

    [Fact]
    public async Task Findings_past_either_cap_are_refused_naming_the_setting_and_queue_nothing()
    {
        var h = new Harness();
        h.Settings["pr.maxFindings"] = "3";
        h.Settings["pr.maxNits"] = "1";
        // One finding already on the pull request, posted in two places: it
        // counts once.
        h.OnThePr.Add(Harness.IldFinding("4001", "0123456789abcdef", "important"));
        h.OnThePr.Add(Harness.IldFinding("4002", "0123456789abcdef", "important", resolved: true));
        var service = h.Build();

        var nit = await service.FindingAsync("wi-1", "src/A.cs", 1, "nit", "Trailing space.", h.Run.Id);
        Assert.True(nit.Ok, nit.Message);

        var secondNit = await service.FindingAsync("wi-1", "src/A.cs", 2, "nit", "Typo in the comment.", h.Run.Id);
        Assert.False(secondNit.Ok);
        Assert.Contains("pr.maxNits", secondNit.Message);
        Assert.Single(h.Queue);

        var important = await service.FindingAsync("wi-1", "src/A.cs", 3, "important", "This can deadlock.", h.Run.Id);
        Assert.True(important.Ok, important.Message);

        var overCap = await service.FindingAsync("wi-1", "src/A.cs", 4, "important", "This swallows the error.", h.Run.Id);
        Assert.False(overCap.Ok);
        Assert.Contains("pr.maxFindings", overCap.Message);
        Assert.Equal(2, h.Queue.Count);
        h.VerifyNothingWritten();
    }

    // -- Sending ---------------------------------------------------------------

    /// <summary>Runs the PR node on the harness's run, which already has its pull request.</summary>
    private static async Task<List<NodeOutcome>> RunPrNodeAsync(Harness h)
    {
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

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(h.Remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        services.AddSingleton(h.Runs.Object);

        var node = new LoopNode { Id = h.Run.CurrentNodeId!.Value, NodeType = NodeType.PR, Config = "{}" };
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new PRNodeExecutor().ExecuteAsync(
            new NodeExecutionContext(h.Run, node, services.BuildServiceProvider(), CancellationToken.None)))
            outcomes.Add(o);
        return outcomes;
    }

    /// <summary>The heartbeat one tick later, finding <paramref name="item"/> on the pull request.</summary>
    private static async Task<Mock<ILoopEngine>> PollAfterAsync(LoopRun sent, RemotePrReviewItem item)
    {
        var loopNodeId = sent.CurrentNodeId!.Value;
        var run = new LoopRun
        {
            Id = sent.Id,
            WorkItemId = sent.WorkItemId,
            PrUrl = sent.PrUrl,
            CurrentNodeId = loopNodeId,
            Status = LoopRunStatus.WaitingHuman,
            HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge,
            PrCommentLedger = sent.PrCommentLedger,
        };

        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetPrAwaitingMergeRunsAsync()).ReturnsAsync(new[] { run });
        runs.Setup(s => s.GetRunNodeAsync(run.Id, loopNodeId)).ReturnsAsync(new LoopRunNode
        {
            Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = loopNodeId, Status = LoopRunNodeStatus.WaitingHuman,
        });
        runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new[]
        {
            new LoopNodeEdge
            {
                Id = Guid.NewGuid(), SourceNodeId = loopNodeId, TargetNodeId = Guid.NewGuid(),
                EdgeType = EdgeType.Custom, Name = LoopOutputs.OnComment,
            },
        });
        runs.Setup(s => s.GetPrCommentLedgerAsync(run.Id)).ReturnsAsync(() => run.PrCommentLedger);
        runs.Setup(s => s.TrySetPrCommentLedgerAsync(run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) =>
            {
                if (!string.Equals(expected, run.PrCommentLedger, StringComparison.Ordinal)) return false;
                run.PrCommentLedger = json;
                return true;
            });

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestSnapshotAsync(RepoUrl, "7"))
            .ReturnsAsync(new RemotePrSnapshot("t", "b", "open", false, null, null, RemotePrCiStatus.None,
                Array.Empty<RemotePrCheck>(), false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow));
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7"))
            .ReturnsAsync(new RemotePrReviewLedger(Array.Empty<RemotePrReviewSummary>(), new[] { item }, Head, null));

        var engine = new Mock<ILoopEngine>();
        await new PrStatusPollService(runs.Object, remote.Object, engine.Object, Mock.Of<IRunNotifier>(),
            NullLogger<PrStatusPollService>.Instance).PollOnceAsync();
        return engine;
    }

    private static RemotePrReviewItem ReviewComment(string id, string body)
        => new("review", id, $"PRRT_{id}", "r9", "src/A.cs", 12, body, "ild-service-account", Head,
            DateTime.UtcNow.AddSeconds(1), false, false);

    [Fact]
    public async Task A_queued_finding_goes_out_on_its_line_marked_as_ilds_and_never_fires_on_comment()
    {
        var h = new Harness();
        var queued = await h.Build().FindingAsync("wi-1", "src/A.cs", 12, "important", "This leaks the file handle.", h.Run.Id);
        Assert.True(queued.Ok, queued.Message);
        var findingId = Assert.Single(h.Queue).FindingId!;

        string? posted = null;
        h.Remote.Setup(r => r.CreateReviewCommentAsync(CloneUrl, "7", "src/A.cs", 12, It.IsAny<string>()))
            .Callback<string, string, string, int, string>((_, _, _, _, body) => posted = body)
            .ReturnsAsync(new RemotePrWriteResult(true, "5001", null));

        var outcomes = await RunPrNodeAsync(h);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, outcomes.OfType<NodeOutcome.WaitingAction>().Single().Reason);
        Assert.Empty(h.Queue);
        Assert.NotNull(posted);
        Assert.Contains("This leaks the file handle.", posted!);
        Assert.True(PrCommentMarker.IsStamped(posted), $"the posted finding carries no marker:\n{posted}");
        Assert.Contains(findingId, posted);

        // Coming back as it was posted, and with its marker edited away: the run
        // wrote it either way, so neither is a comment for it to act on.
        var asPosted = await PollAfterAsync(h.Run, ReviewComment("5001", posted));
        asPosted.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
        var markerGone = await PollAfterAsync(h.Run, ReviewComment("5001", "This leaks the file handle."));
        markerGone.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);

        // Someone else saying the same thing on the same line still fires.
        var someoneElse = await PollAfterAsync(h.Run, ReviewComment("5002", "This leaks the file handle."));
        someoneElse.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.Is<NodeSignal>(s => s.EdgeName == LoopOutputs.OnComment)), Times.Once);
    }

    [Fact]
    public async Task A_finding_the_forge_refuses_does_not_fail_the_node()
    {
        var h = new Harness();
        var queued = await h.Build().FindingAsync("wi-1", "src/A.cs", 9000, "nit", "Off the end of the diff.", h.Run.Id);
        Assert.True(queued.Ok, queued.Message);
        h.Remote.Setup(r => r.CreateReviewCommentAsync(CloneUrl, "7", "src/A.cs", 9000, It.IsAny<string>()))
            .ReturnsAsync(new RemotePrWriteResult(false, null, "HTTP 422: line must be part of the diff"));

        var outcomes = await RunPrNodeAsync(h);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.Empty(h.Queue);
        h.Remote.Verify(r => r.CreateReviewCommentAsync(CloneUrl, "7", "src/A.cs", 9000, It.IsAny<string>()), Times.Once);
    }
}
