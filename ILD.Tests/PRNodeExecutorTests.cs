using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class PRNodeExecutorTests
{
    [Fact]
    public async Task creates_PR_when_PrUrl_is_null()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test" };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";

        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null));

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "wi", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        var refreshed = db.Fresh().LoopRuns.First(r => r.Id == run.Id);
        Assert.Equal(prUrl, refreshed.PrUrl);
    }

    [Fact]
    public async Task pushes_branch_before_creating_PR()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var worktreePath = Path.Combine(Path.GetTempPath(), "ild-test-wt");
        Directory.CreateDirectory(worktreePath);
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test", WorktreePath = worktreePath };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";

        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null));

        var mockRepoManager = new Mock<IRepositoryManager>();
        mockRepoManager.Setup(r => r.GetDiffAsync(worktreePath)).ReturnsAsync((string?)null);
        mockRepoManager.Setup(r => r.PushAsync(worktreePath, "ild/test", CancellationToken.None, It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((true, (string?)null));
        mockRepoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>())).ReturnsAsync(true);
        mockRepoManager.Setup(r => r.GetCommitsAheadCountAsync(worktreePath, "origin/main")).ReturnsAsync(1);

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(mockRepoManager.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "wi", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        mockRepoManager.Verify(r => r.GetDiffAsync(worktreePath), Times.Once);
        mockRepoManager.Verify(r => r.CommitAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockRepoManager.Verify(r => r.PushAsync(
            worktreePath,
            "ild/test",
            CancellationToken.None,
            It.Is<GitAuthOptions>(a => a.ApiKey == remote.ApiKey && a.ProviderType == remote.Type)), Times.Once);
        mockRemote.Verify(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        Directory.Delete(worktreePath, recursive: true);
    }

    [Fact]
    public async Task commits_uncommitted_changes_before_pushing()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var worktreePath = Path.Combine(Path.GetTempPath(), "ild-test-wt");
        Directory.CreateDirectory(worktreePath);
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test", WorktreePath = worktreePath };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";

        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null));

        var mockRepoManager = new Mock<IRepositoryManager>();
        mockRepoManager.Setup(r => r.GetDiffAsync(worktreePath)).ReturnsAsync("--- some diff");
        mockRepoManager.Setup(r => r.CommitAsync(worktreePath, "fix: do a thing")).ReturnsAsync(true);
        mockRepoManager.Setup(r => r.PushAsync(worktreePath, "ild/test", CancellationToken.None, It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((true, (string?)null));
        mockRepoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>())).ReturnsAsync(true);
        mockRepoManager.Setup(r => r.GetCommitsAheadCountAsync(worktreePath, "origin/main")).ReturnsAsync(1);

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(mockRepoManager.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "fix: do a thing", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        mockRepoManager.Verify(r => r.GetDiffAsync(worktreePath), Times.Once);
        mockRepoManager.Verify(r => r.CommitAsync(worktreePath, "fix: do a thing"), Times.Once);
        mockRepoManager.Verify(r => r.PushAsync(worktreePath, "ild/test", CancellationToken.None, It.IsAny<GitAuthOptions?>()), Times.Once);

        Directory.Delete(worktreePath, recursive: true);
    }

    [Fact]
    public async Task fails_when_branch_has_no_commits_ahead_of_target()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var worktreePath = Path.Combine(Path.GetTempPath(), "ild-test-wt");
        Directory.CreateDirectory(worktreePath);
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test", WorktreePath = worktreePath };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var mockRemote = new Mock<IRemoteProvider>();

        var mockRepoManager = new Mock<IRepositoryManager>();
        mockRepoManager.Setup(r => r.GetDiffAsync(worktreePath)).ReturnsAsync((string?)null);
        mockRepoManager.Setup(r => r.PushAsync(worktreePath, "ild/test", CancellationToken.None, It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((true, (string?)null));
        mockRepoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>())).ReturnsAsync(true);
        mockRepoManager.Setup(r => r.GetCommitsAheadCountAsync(worktreePath, "origin/main")).ReturnsAsync(0);

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(mockRepoManager.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "wi", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.False(result.Success);
        Assert.Contains("no commits ahead", result.Error);
        mockRemote.Verify(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        Directory.Delete(worktreePath, recursive: true);
    }

    [Fact]
    public async Task reuses_existing_PR_when_PrUrl_is_set()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var existingPrUrl = "https://gitea.example/r/pull/42";
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test", PrUrl = existingPrUrl };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var mockRemote = new Mock<IRemoteProvider>();

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "wi", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal(existingPrUrl, result.Output);
        mockRemote.Verify(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task resolves_prDescriptionTemplate_placeholders_before_creating_PR()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test" };
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = version.Id,
            NodeType = NodeType.PR,
            Label = "pr",
            Config = "{\"prDescriptionTemplate\":\"## {{WorkItem.Title}}\\n\\n{{WorkItem.Description}}\\n\\nPrevious output: {{PreviousNode.Output}}\"}",
        };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";
        string? capturedBody = null;
        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string __, string ___, string ____, string body) =>
            {
                capturedBody = body;
                return new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null);
            });

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "Fix login bug", Description = "Original description", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, "AI output here", CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Contains("## Fix login bug", capturedBody);
        Assert.Contains("Original description", capturedBody);
        Assert.Contains("AI output here", capturedBody);
    }

    [Fact]
    public async Task falls_back_to_workItem_description_when_no_prDescriptionTemplate()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test" };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";
        string? capturedBody = null;
        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string __, string ___, string ____, string body) =>
            {
                capturedBody = body;
                return new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null);
            });

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "wi", Description = "fallback description", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal("fallback description", capturedBody);
    }

    [Fact]
    public async Task push_failure_includes_git_stderr()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var worktreePath = Path.Combine(Path.GetTempPath(), "ild-test-wt");
        Directory.CreateDirectory(worktreePath);
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test", WorktreePath = worktreePath };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var mockRemote = new Mock<IRemoteProvider>();

        var mockRepoManager = new Mock<IRepositoryManager>();
        mockRepoManager.Setup(r => r.GetDiffAsync(worktreePath)).ReturnsAsync((string?)null);
        mockRepoManager.Setup(r => r.PushAsync(worktreePath, "ild/test", CancellationToken.None, It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((false, "fatal: Authentication failed for 'https://git.kube/Tony/Ild.git'"));

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(mockRepoManager.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "wi", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.False(result.Success);
        Assert.Contains("Authentication failed", result.Error);
        mockRemote.Verify(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        Directory.Delete(worktreePath, recursive: true);
    }

    [Fact]
    public async Task logs_prompt_to_event_log_when_prompt_is_configured()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test" };
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = version.Id,
            NodeType = NodeType.PR,
            Label = "pr",
            Config = "{\"prompt\":\"Please review the PR for {{WorkItem.Title}}\"}",
        };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";
        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null));
        var mockEventLog = new Mock<IEventLogService>();
        var mockRendering = new Mock<IPromptRenderingService>();
        mockRendering.Setup(r => r.RenderAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<WorkItemView>(), It.IsAny<string>()))
            .ReturnsAsync("Please review the PR for Fix login bug");

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(mockEventLog.Object);
        services.AddSingleton(mockRendering.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "Fix login bug", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        mockEventLog.Verify(e => e.AppendAsync(
            run.Id,
            PRNodeExecutor.PrPromptRenderedEvent,
            It.Is<string>(s => s.Contains("Fix login bug")),
            node.Id,
            It.IsAny<string>(),
            runNode.Id),
            Times.Once);
    }

    [Fact]
    public async Task does_not_log_prompt_to_event_log_when_prompt_is_not_configured()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running, RepositoryId = repo.Id, BranchName = "ild/test" };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";
        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null));
        var mockEventLog = new Mock<IEventLogService>();

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(mockEventLog.Object);
        services.AddSingleton((ILoopRunStore)db.LoopRuns);
        services.AddSingleton((IProviderStore)db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = workItemId, Title = "wi", RepositoryId = repo.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        mockEventLog.Verify(e => e.AppendAsync(
            It.IsAny<Guid>(),
            PRNodeExecutor.PrPromptRenderedEvent,
            It.IsAny<string>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<Guid>()),
            Times.Never);
    }
}
