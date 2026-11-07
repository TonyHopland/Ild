using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A service's health URL names its own machine's loopback. When the preview runs in a run's container or pod, ILD
/// checks it where the preview proxy reaches it, at the address the run host gives, not at its own loopback.
/// </summary>
public sealed class WorktreePreviewServiceHealthUrlTests
{
    private const string Worktree = "/ild-run/0123456789abcdef0123456789abcdef/repo";

    private static IRunHost HostAnswering(string target)
    {
        var host = new Mock<IRunHost>();
        host.Setup(h => h.PreviewTargetAsync(Worktree, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, int port, CancellationToken _) => new Uri($"http://{target}:{port}"));
        return host.Object;
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/health")]
    [InlineData("http://localhost:3000/health")]
    [InlineData("http://0.0.0.0:3000/health")]
    public async Task A_loopback_health_url_is_checked_at_the_runs_address(string healthUrl)
    {
        var probed = await WorktreePreviewService.ReachableHealthUrlAsync(HostAnswering("10.89.3.2"), healthUrl, Worktree, CancellationToken.None);

        Assert.Equal("http://10.89.3.2:3000/health", probed);
    }

    [Fact]
    public async Task On_ilds_own_machine_the_url_is_checked_as_written()
    {
        var probed = await WorktreePreviewService.ReachableHealthUrlAsync(HostAnswering("127.0.0.1"), "http://127.0.0.1:3000/health?deep=1", Worktree, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:3000/health?deep=1", probed);
    }

    [Fact]
    public async Task A_health_url_on_another_host_is_checked_as_written()
    {
        var probed = await WorktreePreviewService.ReachableHealthUrlAsync(HostAnswering("10.89.3.2"), "https://status.example.test/ping", Worktree, CancellationToken.None);

        Assert.Equal("https://status.example.test/ping", probed);
    }
}
