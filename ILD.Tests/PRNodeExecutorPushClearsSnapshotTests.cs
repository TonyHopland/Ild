using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class PRNodeExecutorPushClearsSnapshotTests
{
    private sealed record Setup(Mock<ILoopRunStore> Runs, Mock<IRunNotifier> RunNotifier, Mock<IWorkItemNotifier> Board, LoopRun Run);

    private static async Task<Setup> RunAsync(bool pushSucceeds)
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var worktree = Path.Combine(Path.GetTempPath(), "ild-pr-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(m => m.GetDiffAsync(worktree)).ReturnsAsync(string.Empty);
            repoManager.Setup(m => m.GetHeadCommitAsync(worktree)).ReturnsAsync("pushed-sha");
            repoManager.Setup(m => m.PushAsync(worktree, It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(pushSucceeds ? (true, (string?)null) : (false, "rejected"));

            var runs = new Mock<ILoopRunStore>();
            var runNotifier = new Mock<IRunNotifier>();
            var board = new Mock<IWorkItemNotifier>();
            var services = new ServiceCollection();
            services.AddSingleton(workItems.Object);
            services.AddSingleton(providerStore.Object);
            services.AddSingleton(Mock.Of<IRemoteProvider>());
            services.AddSingleton(repoManager.Object);
            services.AddSingleton(runs.Object);
            services.AddSingleton(runNotifier.Object);
            services.AddSingleton(board.Object);
            var sp = services.BuildServiceProvider();

            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                WorktreePath = worktree,
                PrUrl = "https://github.com/o/r/pull/7",
                PrSnapshot = "{\"ci\":\"Failed\"}",
            };
            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
            await foreach (var _ in new PRNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            {
            }
            return new Setup(runs, runNotifier, board, run);
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_successful_push_clears_the_previous_commits_snapshot_and_records_the_pushed_commit()
    {
        var s = await RunAsync(pushSucceeds: true);

        Assert.Null(s.Run.PrSnapshot);
        s.Runs.Verify(r => r.RecordPrPushAsync(s.Run.Id, "pushed-sha"), Times.Once);
        s.RunNotifier.Verify(n => n.PrSnapshotChangedAsync(s.Run.Id), Times.Once);
        s.Board.Verify(n => n.PrStatusChangedAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_push_leaves_the_snapshot_alone()
    {
        var s = await RunAsync(pushSucceeds: false);

        Assert.NotNull(s.Run.PrSnapshot);
        s.Runs.Verify(r => r.RecordPrPushAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        s.Board.Verify(n => n.PrStatusChangedAsync(It.IsAny<string>()), Times.Never);
    }
}
