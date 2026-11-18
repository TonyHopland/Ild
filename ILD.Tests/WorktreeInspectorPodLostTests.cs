using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>A worktree tool on a run whose pod is gone says so, with the way on, instead of failing as a git command.</summary>
public class WorktreeInspectorPodLostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_read_in_a_run_whose_pod_is_gone_answers_no_worktree_with_the_lost_text()
    {
        var lost = new RunPodLostException(Guid.NewGuid(), RunTermination.LostText("pod"));
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync("wi-1")).ReturnsAsync(new WorkItemView { Id = "wi-1", WorktreePath = "/ild-run/x/repo" });
        var files = new Mock<IRunFiles>();
        files.Setup(f => f.DirectoryExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var repos = new Mock<IRepositoryManager>();
        repos.Setup(r => r.ValidateWorktreeHealthAsync(It.IsAny<string>())).ReturnsAsync(true);
        repos.Setup(r => r.ReadGitAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("could not run a command", lost));
        var inspector = new WorktreeInspector(workItems.Object, repos.Object, files.Object, new Mock<ILoopRunStore>().Object, new Mock<IProviderStore>().Object);

        var result = await inspector.GetStatusAsync("wi-1", Ct);

        Assert.Equal(WorktreeReadOutcome.NoWorktree, result.Outcome);
        Assert.Equal(lost.Message, result.Message);
    }
}
