using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class PRNodeExecutorTests
{
    [Fact]
    public async Task When_PR_exists_and_PrCommentTemplate_is_set_posts_rendered_comment()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView
        {
            Id = "WI-1",
            Title = "Title",
            Description = "Body",
            RepositoryId = repoId,
        };
        var repo = new Repository
        {
            Id = repoId,
            Name = "repo",
            CloneUrl = "https://example.com/owner/repo.git",
            DefaultBranch = "main",
            RemoteProviderId = Guid.NewGuid(),
        };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestCommentAsync("https://example.com/owner/repo.git", "42", It.IsAny<string>()))
            .ReturnsAsync(true);

        var rendering = new Mock<IPromptRenderingService>();
        rendering.Setup(r => r.RenderAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<WorkItemView>(), It.IsAny<string?>()))
            .ReturnsAsync((string template, Guid _, WorkItemView _, string? _) => template.Replace("{{WorkItem.Title}}", "Title"));

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(rendering.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            NodeType = NodeType.PR,
            Config = """{"prCommentTemplate":"Update on {{WorkItem.Title}}"}""",
        };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            PrUrl = "https://example.com/owner/repo/pull/42",
        };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        remote.Verify(r => r.CreatePullRequestCommentAsync(
            "https://example.com/owner/repo.git", "42", "Update on Title"), Times.Once);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrCreated);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task When_PR_exists_without_PrCommentTemplate_skips_comment_and_parks()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", PrUrl = "https://example.com/o/r/pull/7" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        // Strict mock — verifies the remote was never invoked at all.
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task When_prompt_template_set_renders_it_to_announce_and_park()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "Title", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        // Strict — the existing PR with no comment template means the remote is never touched.
        var remote = new Mock<IRemoteProvider>(MockBehavior.Strict);

        var rendering = new Mock<IPromptRenderingService>();
        rendering.Setup(r => r.RenderAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<WorkItemView>(), It.IsAny<string?>()))
            .ReturnsAsync((string template, Guid _, WorkItemView _, string? _) => template.Replace("{{WorkItem.Title}}", "Title"));

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(rendering.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = """{"prompt":"Please merge {{WorkItem.Title}}"}""" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", PrUrl = "https://example.com/o/r/pull/7" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        // The rendered template — not the raw template or the PR URL — is both
        // announced as the node input and surfaced as the parked node's content.
        var starting = outcomes.OfType<NodeOutcome.NodeStarting>().Single();
        Assert.Equal("Please merge Title", starting.EffectiveInput);
        var waiting = outcomes.OfType<NodeOutcome.WaitingAction>().Single();
        Assert.Equal(HumanFeedbackReasons.PrAwaitingMerge, waiting.Reason);
        Assert.Equal("Please merge Title", waiting.Output);
    }

    [Fact]
    public async Task When_no_prompt_template_parks_with_pr_url()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", PrUrl = "https://example.com/o/r/pull/7" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        // No prompt template to render — the PR URL is kept as the parked content.
        var waiting = outcomes.OfType<NodeOutcome.WaitingAction>().Single();
        Assert.Equal("https://example.com/o/r/pull/7", waiting.Output);
    }

    [Fact]
    public async Task PR_targets_the_base_branch_the_run_was_built_from_not_the_repository_default()
    {
        // "Continue on a branch" and "hotfix a branch" only make sense if the
        // work goes back to that branch. The Start node rebased the worktree
        // onto it; opening the PR against main would ask for a diff nobody
        // wanted.
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestAsync(repo.CloneUrl, It.IsAny<string>(), "release/1.0", "T", It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(null, "https://example.com/o/r/pull/42", RemotePrStatus.Open, null));

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", BaseBranchOverride = "release/1.0" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.PrCreated);
        remote.Verify(r => r.CreatePullRequestAsync(
            repo.CloneUrl, It.IsAny<string>(), "release/1.0", "T", It.IsAny<string>()), Times.Once);
        remote.Verify(r => r.CreatePullRequestAsync(
            It.IsAny<string>(), It.IsAny<string>(), "main", It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task The_commits_ahead_guard_measures_against_the_run_base_branch()
    {
        // The guard exists to stop an empty PR. Measured against origin/main it
        // would count every commit on release/1.0 as "ahead" and wave through a
        // branch that has nothing of its own.
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var worktree = Path.Combine(Path.GetTempPath(), "ild-pr-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(m => m.GetDiffAsync(worktree)).ReturnsAsync(string.Empty);
            repoManager.Setup(m => m.PushAsync(worktree, It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync((true, (string?)null));
            repoManager.Setup(m => m.GetCommitsAheadCountAsync(worktree, "origin/release/1.0")).ReturnsAsync(0);
            // Against the repository default the branch would look non-empty.
            repoManager.Setup(m => m.GetCommitsAheadCountAsync(worktree, "origin/main")).ReturnsAsync(9);

            var services = new ServiceCollection();
            services.AddSingleton(workItems.Object);
            services.AddSingleton(providerStore.Object);
            services.AddSingleton(Mock.Of<IRemoteProvider>());
            services.AddSingleton(repoManager.Object);
            var sp = services.BuildServiceProvider();

            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                WorktreePath = worktree,
                BaseBranchOverride = "release/1.0",
            };

            var executor = new PRNodeExecutor();
            var outcomes = new List<NodeOutcome>();
            await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
                outcomes.Add(o);

            var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
            Assert.Contains("origin/release/1.0", fail.Reason);
            Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrCreated);
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task When_creating_PR_and_AutoMerge_tag_present_enables_auto_merge()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView
        {
            Id = "WI-1",
            Title = "T",
            Description = "D",
            RepositoryId = repoId,
            Tags = new[] { "AutoMerge" },
        };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestAsync(repo.CloneUrl, It.IsAny<string>(), "main", "T", It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(null, "https://example.com/o/r/pull/42", RemotePrStatus.Open, null));
        remote.Setup(r => r.EnablePullRequestAutoMergeAsync(repo.CloneUrl, "42")).ReturnsAsync(true);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        // No worktree path → the commit/push prep is skipped and the node goes
        // straight to PR creation, where auto-merge is turned on.
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.Contains(outcomes, o => o is NodeOutcome.PrCreated);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        remote.Verify(r => r.EnablePullRequestAutoMergeAsync(repo.CloneUrl, "42"), Times.Once);
    }

    [Fact]
    public async Task When_creating_PR_without_AutoMerge_tag_does_not_enable_auto_merge()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(null, "https://example.com/o/r/pull/42", RemotePrStatus.Open, null));

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.Contains(outcomes, o => o is NodeOutcome.PrCreated);
        remote.Verify(r => r.EnablePullRequestAutoMergeAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AutoMerge_tag_is_matched_case_insensitively()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView
        {
            Id = "WI-1",
            Title = "T",
            Description = "D",
            RepositoryId = repoId,
            Tags = new[] { "automerge" },
        };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(null, "https://example.com/o/r/pull/7", RemotePrStatus.Open, null));
        remote.Setup(r => r.EnablePullRequestAutoMergeAsync(repo.CloneUrl, "7")).ReturnsAsync(true);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        remote.Verify(r => r.EnablePullRequestAutoMergeAsync(repo.CloneUrl, "7"), Times.Once);
    }

    [Fact]
    public async Task When_PR_exists_and_comment_post_fails_node_fails()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = """{"prCommentTemplate":"hi"}""" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", PrUrl = "https://example.com/o/r/pull/9" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Contains("PR comment failed", fail.Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WaitingAction);
    }

    [Theory]
    [InlineData("CI failed on this build, twice")]
    [InlineData(null)]
    public async Task Re_entry_on_a_pr_edge_hands_the_next_node_a_reason(string? signalOutput)
    {
        // Whatever the signal carried is what the next node reads as
        // {{PreviousNode.Output}} — and a signal that carried nothing (a human
        // firing the edge from the feedback UI) falls back to the last polled
        // snapshot, so it says as much as the heartbeat would have.
        var (sp, node) = ReEntryContext();
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            PrUrl = "https://example.com/o/r/pull/7",
            PrSnapshot = PrSnapshotJson.Serialize(new RemotePrSnapshot(
                "t", "b", "open", false, null, null, RemotePrCiStatus.Failed,
                new[] { new RemotePrCheck("build", "failure", "https://ci/build", "tsc: 3 errors", "991") },
                false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow)),
            ExternalActionResult = signalOutput ?? string.Empty,
            ExternalActionResultType = ExternalActionResultType.Success,
            ExternalActionEdgeName = PrNodeEdges.OnCiFailed,
        };

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new PRNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        var success = outcomes.OfType<NodeOutcome.Success>().Single();
        Assert.Equal(EdgeType.Custom, success.Edge);
        Assert.Equal(PrNodeEdges.OnCiFailed, success.EdgeName);
        Assert.False(string.IsNullOrWhiteSpace(success.Output));
        if (signalOutput is null)
            Assert.Contains("build", success.Output);
        else
            Assert.Equal(signalOutput, success.Output);
        // Re-entry must not re-announce the node or re-park it.
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.NodeStarting or NodeOutcome.WaitingAction);
    }

    [Fact]
    public async Task Re_entry_with_no_signal_text_and_no_snapshot_still_says_what_happened()
    {
        var (sp, node) = ReEntryContext();
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            PrUrl = "https://example.com/o/r/pull/7",
            ExternalActionResult = string.Empty,
            ExternalActionResultType = ExternalActionResultType.Success,
            ExternalActionEdgeName = PrNodeEdges.OnCiFailed,
        };

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new PRNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.Equal(PrNodeEdges.Describe(PrNodeEdges.OnCiFailed),
            outcomes.OfType<NodeOutcome.Success>().Single().Output);
    }

    /// <summary>A PR node whose PR already exists, with the remote left strict — re-entry touches neither.</summary>
    private static (IServiceProvider Services, LoopNode Node) ReEntryContext()
    {
        var repoId = Guid.NewGuid();
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync(new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId });
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(new Repository
        {
            Id = repoId,
            Name = "r",
            CloneUrl = "https://example.com/o/r.git",
            DefaultBranch = "main",
            RemoteProviderId = Guid.NewGuid(),
        });

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(new Mock<IRemoteProvider>(MockBehavior.Strict).Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        return (services.BuildServiceProvider(),
            new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" });
    }
}
