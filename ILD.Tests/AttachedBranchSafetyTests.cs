using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A run attached to an existing branch works on someone else's history: it must
/// never push onto the branch its pull request targets, must not fail on a branch
/// the author moved on under it while it had nothing of its own to push, and must
/// not lose its first routing to a stop between attaching and routing on.
/// </summary>
public class AttachedBranchSafetyTests : IDisposable
{
    private const string CloneUrl = "https://example.com/o/r.git";
    private const string Branch = "feature/x";
    private const string PrUrl = "https://example.com/o/r/pull/17";
    private readonly string _baseRepo = Path.Combine(Path.GetTempPath(), "ild-attach-" + Guid.NewGuid().ToString("N"));

    public AttachedBranchSafetyTests() => Directory.CreateDirectory(Path.Combine(_baseRepo, ".git"));

    public void Dispose()
    {
        try { Directory.Delete(_baseRepo, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private (IServiceProvider Services, Mock<IRepositoryManager> Git, Mock<IRemoteProvider> Remote, Mock<ILoopRunStore> Runs) Services()
    {
        var repoId = Guid.NewGuid();
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync("WI-1"))
            .ReturnsAsync(new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId });
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(new Repository
        {
            Id = repoId, Name = "r", CloneUrl = CloneUrl, DefaultBranch = "main", WorktreesPath = _baseRepo,
            RemoteProviderId = Guid.NewGuid(),
        });
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var git = new Mock<IRepositoryManager>();
        git.Setup(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>())).ReturnsAsync(true);
        git.Setup(m => m.ResetHardAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        git.Setup(m => m.RemoteBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        git.Setup(m => m.GetDiffAsync(It.IsAny<string>())).ReturnsAsync(string.Empty);
        git.Setup(m => m.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((true, (string?)null));

        var remote = new Mock<IRemoteProvider>();
        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(Array.Empty<LoopNodeEdge>());

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(git.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(runs.Object);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(NoPackageFeeds.Resolver);
        return (services.BuildServiceProvider(), git, remote, runs);
    }

    private static async Task<List<NodeOutcome>> RunAsync(INodeExecutor executor, LoopRun run, NodeType type, IServiceProvider sp)
    {
        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = type, Config = "{}" };
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);
        return outcomes;
    }

    [Theory]
    [InlineData(null, "main")]
    [InlineData("release/1.0", "release/1.0")]
    public async Task An_attaching_run_refuses_the_branch_its_pull_request_would_target(string? baseOverride, string branch)
    {
        var (sp, git, _, _) = Services();
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "WI-1", BranchName = branch, AttachToExistingBranch = true,
            BaseBranchOverride = baseOverride,
        };

        var outcomes = await RunAsync(new StartNodeExecutor(), run, NodeType.Start, sp);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Contains(branch, fail.Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WorktreeReady);
        git.Verify(m => m.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task An_attached_branch_is_pushed_only_when_the_run_has_commits_of_its_own_on_it(int ahead, bool pushed)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_baseRepo, "wt")).FullName;
        var (sp, git, remote, _) = Services();
        git.Setup(m => m.GetCommitsAheadCountAsync(worktree, $"origin/{Branch}")).ReturnsAsync(ahead);
        // Behind its author's latest push: a push now would be refused.
        git.Setup(m => m.PushAsync(worktree, Branch, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(pushed ? (true, (string?)null) : (false, "rejected: non-fast-forward"));
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "WI-1", BranchName = Branch, AttachToExistingBranch = true,
            WorktreePath = worktree, PrUrl = PrUrl,
        };

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        git.Verify(m => m.PushAsync(worktree, Branch, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()),
            pushed ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Attaching_records_the_pull_request_only_after_everything_else_it_does()
    {
        var (sp, _, remote, runs) = Services();
        var order = new List<string>();
        remote.Setup(r => r.FindOpenPullRequestAsync(CloneUrl, Branch, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemotePrLookup(17, PrUrl, PrUrl, null));
        string? queue = PrCommentQueueJson.Serialize(new[]
        {
            new PrQueuedWrite("w1", PrQueuedWrite.Comment, string.Empty, "hello", null, null, DateTime.UtcNow),
        });
        string? ledger = null;
        runs.Setup(s => s.GetPrCommentQueueAsync(It.IsAny<Guid>())).ReturnsAsync(() => queue);
        runs.Setup(s => s.TrySetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) => { queue = json; return true; });
        runs.Setup(s => s.GetPrCommentLedgerAsync(It.IsAny<Guid>())).ReturnsAsync(() => ledger);
        runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback(() => order.Add("ledger"))
            .ReturnsAsync((Guid _, string? expected, string? json) => { ledger = json; return true; });
        remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "17", It.IsAny<string>()))
            .Callback(() => order.Add("comment"))
            .ReturnsAsync(new RemotePrWriteResult(true, "900", null));
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", BranchName = Branch };

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        await foreach (var o in new PRNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            if (o is NodeOutcome.PrAttached) order.Add("attached");

        Assert.Equal("attached", order[^1]);
        Assert.Contains("ledger", order);
        Assert.Contains("comment", order);
    }

    private static RemotePrSnapshot SnapshotAt(string head)
        => new("t", "b", "open", false, null, null, RemotePrCiStatus.None, Array.Empty<RemotePrCheck>(), false, false,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow, head);

    [Fact]
    public async Task Attaching_hands_the_engine_the_pull_request_as_the_run_found_it()
    {
        var (sp, _, remote, runs) = Services();
        remote.Setup(r => r.FindOpenPullRequestAsync(CloneUrl, Branch, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemotePrLookup(17, PrUrl, PrUrl, null));
        remote.Setup(r => r.GetPullRequestSnapshotAsync(CloneUrl, "17", It.IsAny<CancellationToken>())).ReturnsAsync(SnapshotAt("abc123"));
        runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(true);
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", BranchName = Branch };

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        var seen = outcomes.FindIndex(o => o is NodeOutcome.PrSeen { HeadSha: "abc123" });
        Assert.InRange(seen, 0, outcomes.FindIndex(o => o is NodeOutcome.PrAttached) - 1);
    }

    [Fact]
    public async Task Creating_hands_the_engine_the_new_pull_request_as_it_stands()
    {
        var (sp, _, remote, runs) = Services();
        remote.Setup(r => r.FindOpenPullRequestAsync(CloneUrl, Branch, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemotePrLookup(null, null, null, null));
        remote.Setup(r => r.CreatePullRequestAsync(CloneUrl, Branch, "main", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(PrUrl, PrUrl, RemotePrStatus.Open, null));
        remote.Setup(r => r.GetPullRequestSnapshotAsync(CloneUrl, "17", It.IsAny<CancellationToken>())).ReturnsAsync(SnapshotAt("abc123"));
        runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(true);
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", BranchName = Branch };

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.Contains(outcomes, o => o is NodeOutcome.PrCreated);
        Assert.Contains(outcomes, o => o is NodeOutcome.PrSeen { HeadSha: "abc123" });
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public async Task A_push_of_the_runs_own_is_seen_so_it_is_not_reported_back_as_new_commits(int ahead, bool seen)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_baseRepo, "wt-seen")).FullName;
        var (sp, git, remote, _) = Services();
        git.Setup(m => m.GetCommitsAheadCountAsync(worktree, $"origin/{Branch}")).ReturnsAsync(ahead);
        git.Setup(m => m.ResolveCommitAsync(worktree, "HEAD")).ReturnsAsync("def456");
        // What the forge reports may not have caught up with the push yet.
        remote.Setup(r => r.GetPullRequestSnapshotAsync(CloneUrl, "17", It.IsAny<CancellationToken>())).ReturnsAsync(SnapshotAt("stale"));
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "WI-1", BranchName = Branch, AttachToExistingBranch = true,
            WorktreePath = worktree, PrUrl = PrUrl,
        };

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.Equal(seen, outcomes.Any(o => o is NodeOutcome.PrSeen { HeadSha: "def456", Snapshot: null }));
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrSeen { HeadSha: "stale" });
        Assert.IsType<NodeOutcome.WaitingAction>(outcomes[^1]);
    }

    private LoopRun AttachedRunWithPr(string worktreeName, bool attached = true) => new()
    {
        Id = Guid.NewGuid(), WorkItemId = "WI-1", BranchName = Branch, AttachToExistingBranch = attached,
        WorktreePath = Directory.CreateDirectory(Path.Combine(_baseRepo, worktreeName)).FullName, PrUrl = PrUrl,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commits_of_the_runs_own_are_rebased_onto_an_author_push_before_they_are_pushed(bool attached)
    {
        var (sp, git, _, _) = Services();
        var run = AttachedRunWithPr("wt-behind-" + attached, attached);
        run.PrSnapshot = PrSnapshotJson.Serialize(SnapshotAt("seen1"));
        git.Setup(m => m.GetCommitsAheadCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(1);
        git.Setup(m => m.GetCommitsBehindCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(2);
        // The author pushed past what the run last saw.
        git.Setup(m => m.ResolveCommitAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync("author2");
        git.Setup(m => m.RebaseAsync(run.WorktreePath!, $"origin/{Branch}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RebaseResult(true, Array.Empty<string>(), null));

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        git.Verify(m => m.RebaseAsync(run.WorktreePath!, $"origin/{Branch}", It.IsAny<CancellationToken>()), Times.Once);
        git.Verify(m => m.PushAsync(run.WorktreePath!, Branch, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Once);
    }

    [Fact]
    public async Task A_run_that_rewrote_its_own_pushed_history_is_not_rebased_onto_its_old_commits()
    {
        // Origin is still at the head the run pushed and saw; the run rebased
        // its branch onto its base since. Replaying origin's old commits under
        // it would push copies of the base: the push is left to be refused.
        var (sp, git, _, _) = Services();
        var run = AttachedRunWithPr("wt-rewritten", attached: false);
        run.PrSnapshot = PrSnapshotJson.Serialize(SnapshotAt("pushed1"));
        git.Setup(m => m.GetCommitsAheadCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(5);
        git.Setup(m => m.GetCommitsBehindCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(2);
        git.Setup(m => m.ResolveCommitAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync("pushed1");
        git.Setup(m => m.PushAsync(run.WorktreePath!, Branch, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((false, "rejected: non-fast-forward"));

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        git.Verify(m => m.RebaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("non-fast-forward", outcomes.OfType<NodeOutcome.Fail>().Single().Reason);
    }

    [Fact]
    public async Task A_runs_own_branch_is_pushed_as_before_when_git_reports_nothing_either_way()
    {
        var (sp, git, _, _) = Services();
        var run = AttachedRunWithPr("wt-own-plain", attached: false);

        await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        git.Verify(m => m.PushAsync(run.WorktreePath!, Branch, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Once);
    }

    [Fact]
    public async Task A_rebase_onto_an_author_push_that_conflicts_fails_saying_so_and_pushes_nothing()
    {
        var (sp, git, _, _) = Services();
        var run = AttachedRunWithPr("wt-conflict");
        git.Setup(m => m.ResolveCommitAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync("author2");
        git.Setup(m => m.GetCommitsAheadCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(1);
        git.Setup(m => m.GetCommitsBehindCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(1);
        git.Setup(m => m.RebaseAsync(run.WorktreePath!, $"origin/{Branch}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RebaseResult(false, new[] { "src/A.cs" }, "conflict"));

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("moved on origin", fail.Reason);
        Assert.Contains("src/A.cs", fail.Reason);
        git.Verify(m => m.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
    }

    [Fact]
    public async Task Behind_an_author_push_with_nothing_of_its_own_the_run_pushes_nothing_and_still_sends_its_queue()
    {
        var (sp, git, remote, runs) = Services();
        var run = AttachedRunWithPr("wt-behind-only");
        git.Setup(m => m.GetCommitsBehindCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(3);
        string? queue = PrCommentQueueJson.Serialize(new[]
        {
            new PrQueuedWrite("w1", PrQueuedWrite.Comment, string.Empty, "round findings", null, null, DateTime.UtcNow),
        });
        runs.Setup(s => s.GetPrCommentQueueAsync(It.IsAny<Guid>())).ReturnsAsync(() => queue);
        runs.Setup(s => s.TrySetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) => { queue = json; return true; });
        remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "17", It.IsAny<string>()))
            .ReturnsAsync(new RemotePrWriteResult(true, "900", null));

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        git.Verify(m => m.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
        remote.Verify(r => r.CreatePullRequestCommentAsync(CloneUrl, "17", It.Is<string>(b => b.Contains("round findings"))), Times.Once);
    }

    [Fact]
    public async Task An_attached_branch_that_cannot_be_fetched_is_not_pushed_to()
    {
        var (sp, git, _, _) = Services();
        var run = AttachedRunWithPr("wt-nofetch");
        git.Setup(m => m.FetchAsync(run.WorktreePath!, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>())).ReturnsAsync(false);
        git.Setup(m => m.GetCommitsAheadCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(1);

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.Contains("Could not fetch origin", outcomes.OfType<NodeOutcome.Fail>().Single().Reason);
        git.Verify(m => m.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
    }

    [Fact]
    public async Task A_fetch_failure_with_nothing_of_the_runs_own_to_push_still_sends_the_rounds_findings()
    {
        var (sp, git, remote, runs) = Services();
        var run = AttachedRunWithPr("wt-nofetch-review");
        git.Setup(m => m.FetchAsync(run.WorktreePath!, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>())).ReturnsAsync(false);
        string? queue = PrCommentQueueJson.Serialize(new[]
        {
            new PrQueuedWrite("w1", PrQueuedWrite.Comment, string.Empty, "round findings", null, null, DateTime.UtcNow),
        });
        runs.Setup(s => s.GetPrCommentQueueAsync(It.IsAny<Guid>())).ReturnsAsync(() => queue);
        runs.Setup(s => s.TrySetPrCommentQueueAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string? expected, string? json) => { queue = json; return true; });
        remote.Setup(r => r.CreatePullRequestCommentAsync(CloneUrl, "17", It.IsAny<string>()))
            .ReturnsAsync(new RemotePrWriteResult(true, "900", null));

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        remote.Verify(r => r.CreatePullRequestCommentAsync(CloneUrl, "17", It.Is<string>(b => b.Contains("round findings"))), Times.Once);
        git.Verify(m => m.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
    }

    [Fact]
    public async Task An_attached_branch_deleted_on_origin_is_never_pushed_back()
    {
        var (sp, git, _, _) = Services();
        var run = AttachedRunWithPr("wt-gone");
        git.Setup(m => m.RemoteBranchExistsAsync(run.WorktreePath!, Branch)).ReturnsAsync(false);
        git.Setup(m => m.GetCommitsAheadCountAsync(run.WorktreePath!, It.IsAny<string>())).ReturnsAsync(1);

        var outcomes = await RunAsync(new PRNodeExecutor(), run, NodeType.PR, sp);

        Assert.Contains("no longer exists on origin", outcomes.OfType<NodeOutcome.Fail>().Single().Reason);
        git.Verify(m => m.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
    }

    [Fact]
    public async Task The_visit_that_attaches_reads_the_pull_request_once_and_dates_it_from_the_forge()
    {
        var (sp, git, remote, runs) = Services();
        var opened = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var run = AttachedRunWithPr("wt-attach-once");
        run.PrUrl = null;
        git.Setup(m => m.GetCommitsAheadCountAsync(run.WorktreePath!, $"origin/{Branch}")).ReturnsAsync(1);
        git.Setup(m => m.ResolveCommitAsync(run.WorktreePath!, "HEAD")).ReturnsAsync("pushed1");
        remote.Setup(r => r.FindOpenPullRequestAsync(CloneUrl, Branch, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemotePrLookup(17, PrUrl, PrUrl, null, opened));
        remote.Setup(r => r.GetPullRequestSnapshotAsync(CloneUrl, "17", It.IsAny<CancellationToken>())).ReturnsAsync(SnapshotAt("abc123"));
        runs.Setup(s => s.TrySetPrCommentLedgerAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(true);

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new PRNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
        {
            outcomes.Add(o);
            // As the engine does: the run has its pull request from here on.
            if (o is NodeOutcome.PrAttached attached) run.PrUrl = attached.PrUrl;
        }

        Assert.Equal(opened, outcomes.OfType<NodeOutcome.PrAttached>().Single().CreatedAt);
        // It pushed on this visit, and the forge may not have caught up yet.
        Assert.Equal("pushed1", Assert.Single(outcomes.OfType<NodeOutcome.PrSeen>()).HeadSha);
        remote.Verify(r => r.GetPullRequestSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_forgejo_pull_request_from_a_fork_with_the_same_branch_name_is_not_taken_for_the_runs()
    {
        using var db = new TestDb();
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(), Name = "f", Type = "Forgejo", Url = "https://gitea.example", ApiKey = "k",
        });
        db.Context.SaveChanges();
        var handler = new ListHandler(
            "[{\"number\":3,\"html_url\":\"https://gitea.example/team/repo/pulls/3\","
            + "\"head\":{\"ref\":\"feature\",\"repo\":{\"full_name\":\"someone/repo\"}},\"base\":{\"ref\":\"main\"}},"
            + "{\"number\":5,\"html_url\":\"https://gitea.example/team/repo/pulls/5\","
            + "\"head\":{\"ref\":\"feature\",\"repo\":{\"full_name\":\"team/repo\"}},\"base\":{\"ref\":\"main\"}}]");
        var service = new RemoteProviderService(db.Providers,
            new IRemoteGitProviderAdapter[] { new ForgejoRemoteGitProviderAdapter() }, new HttpClient(handler));

        var found = await service.FindOpenPullRequestAsync("https://gitea.example/team/repo.git", "feature", "release");

        Assert.Null(found.Error);
        Assert.Equal(5, found.Number);
    }

    private sealed class ListHandler(string page) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.Query.Contains("page=1") ? page : "[]", Encoding.UTF8, "application/json"),
            });
    }
}
