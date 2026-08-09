using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The read side of a CI failure: the reason text hands an agent a check id, and
/// this is how it gets from that summary to the actual error without holding any
/// forge credentials.
/// </summary>
public class PrCiLogServiceTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private static LoopRun RunWithFailingCheck(string checkId = "67890", string? url = "https://ci/build")
        => new()
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            PrUrl = PrUrl,
            PrSnapshot = PrSnapshotJson.Serialize(new RemotePrSnapshot(
                "t", "b", "open", false, null, null, RemotePrCiStatus.Failed,
                new[] { new RemotePrCheck("build", "failure", url, "tsc: 3 errors", checkId) },
                false, false, Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow)),
        };

    private static (PrCiLogService Service, Mock<IRemoteProvider> Remote) Build(LoopRun? run)
    {
        var runs = new Mock<ILoopRunStore>();
        runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(run);
        var remote = new Mock<IRemoteProvider>();
        return (new PrCiLogService(runs.Object, remote.Object), remote);
    }

    [Fact]
    public async Task Reads_the_window_for_a_check_named_in_the_runs_own_snapshot()
    {
        var (service, remote) = Build(RunWithFailingCheck());
        remote.Setup(r => r.GetCheckLogAsync("https://github.com/team/repo", "67890", 50, 10))
            .ReturnsAsync(new RemoteCiLog(true, "boom", 1, 10, 900, false, null));

        var log = await service.ReadAsync("wi-1", "67890", tailLines: 50, offset: 10);

        Assert.True(log.Available);
        Assert.Equal("boom", log.Text);
        remote.Verify(r => r.GetCheckLogAsync("https://github.com/team/repo", "67890", 50, 10), Times.Once);
    }

    [Fact]
    public async Task Refuses_a_check_id_that_is_not_on_this_work_items_pull_request()
    {
        // The handle is only honoured where it was handed out, so the tool reads
        // the run's own CI and cannot be pointed at another job in the forge.
        var (service, remote) = Build(RunWithFailingCheck());

        var log = await service.ReadAsync("wi-1", "999999", tailLines: 100, offset: 0);

        Assert.False(log.Available);
        Assert.Contains("999999", log.Message);
        remote.Verify(r => r.GetCheckLogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task An_unavailable_log_still_points_at_where_a_human_can_read_it()
    {
        // The provider knows only the id it was handed; the snapshot is what
        // knows the URL, so the fallback is composed here.
        var (service, remote) = Build(RunWithFailingCheck());
        remote.Setup(r => r.GetCheckLogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(RemoteCiLog.Unavailable("No logs available for Forgejo."));

        var log = await service.ReadAsync("wi-1", "67890", tailLines: 100, offset: 0);

        Assert.False(log.Available);
        Assert.Contains("No logs available for Forgejo.", log.Message);
        Assert.Contains("https://ci/build", log.Message);
    }

    [Fact]
    public async Task An_unavailable_log_with_no_url_recorded_says_only_what_it_knows()
    {
        var (service, remote) = Build(RunWithFailingCheck(url: null));
        remote.Setup(r => r.GetCheckLogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(RemoteCiLog.Unavailable("No logs available."));

        var log = await service.ReadAsync("wi-1", "67890", tailLines: 100, offset: 0);

        Assert.Equal("No logs available.", log.Message);
    }

    [Fact]
    public async Task A_work_item_with_no_pull_request_is_told_so()
    {
        var (service, _) = Build(new LoopRun { Id = Guid.NewGuid(), WorkItemId = "wi-1" });

        var log = await service.ReadAsync("wi-1", "67890", tailLines: 100, offset: 0);

        Assert.False(log.Available);
        Assert.Contains("no pull request", log.Message);
    }

    [Fact]
    public async Task A_work_item_with_no_run_at_all_is_an_answer_not_a_crash()
    {
        var (service, _) = Build(null);

        Assert.False((await service.ReadAsync("wi-1", "67890", 100, 0)).Available);
    }

    [Theory]
    [InlineData(0, PrCiLogService.DefaultTailLines)]
    [InlineData(-5, PrCiLogService.DefaultTailLines)]
    [InlineData(50_000, PrCiLogService.MaxTailLines)]
    [InlineData(50, 50)]
    public async Task Clamps_the_window_an_agent_asks_for(int asked, int expected)
    {
        var (service, remote) = Build(RunWithFailingCheck());
        remote.Setup(r => r.GetCheckLogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new RemoteCiLog(true, "x", 1, 0, 1, false, null));

        await service.ReadAsync("wi-1", "67890", tailLines: asked, offset: -3);

        remote.Verify(r => r.GetCheckLogAsync(It.IsAny<string>(), "67890", expected, 0), Times.Once);
    }
}
